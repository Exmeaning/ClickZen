using ClickZen.Core.Variables;

namespace ClickZen.Core.Tests.Variables;

public sealed class VariableValueTests
{
    [Fact]
    public void Numeric_equality_crosses_types()
    {
        Assert.Equal(VariableValue.FromInt(3), VariableValue.FromDouble(3.0));
        Assert.Equal(VariableValue.FromBool(true), VariableValue.FromInt(1));
        // Numeric text received over the network compares as a number.
        Assert.Equal(VariableValue.FromString("3"), VariableValue.FromInt(3));
        Assert.True(VariableValue.FromString("10").CompareTo(VariableValue.FromInt(9)) > 0);
        // Non-numeric text never equals a number; two strings compare as text.
        Assert.NotEqual(VariableValue.FromString("three"), VariableValue.FromInt(3));
        Assert.NotEqual(VariableValue.FromString("03"), VariableValue.FromString("3"));
        // Large integers keep full precision.
        Assert.NotEqual(VariableValue.FromInt(9_007_199_254_740_993), VariableValue.FromInt(9_007_199_254_740_992));
    }

    [Theory]
    [InlineData("42", VariableType.Int)]
    [InlineData("-7", VariableType.Int)]
    [InlineData("3.5", VariableType.Double)]
    [InlineData("true", VariableType.Bool)]
    [InlineData("hello", VariableType.String)]
    public void Parse_infers_type(string text, VariableType expected) => Assert.Equal(expected, VariableValue.Parse(text).Type);

    [Fact]
    public void Convert_to_declared_type()
    {
        Assert.Equal(VariableType.Int, VariableValue.FromString("12").ConvertTo(VariableType.Int).Type);
        Assert.Equal(12, VariableValue.FromString("12.9").ConvertTo(VariableType.Int).AsInt);
        Assert.True(VariableValue.FromString("yes").ConvertTo(VariableType.Bool).AsBool);
        Assert.False(VariableValue.FromString("false").ConvertTo(VariableType.Bool).AsBool);
        Assert.Equal("2.5", VariableValue.FromDouble(2.5).ConvertTo(VariableType.String).AsString);
    }

    [Fact]
    public void Json_round_trip()
    {
        var values = new[] { VariableValue.FromInt(5), VariableValue.FromDouble(1.25), VariableValue.True, VariableValue.FromString("a\"b") };
        foreach (var v in values)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(v);
            var back = System.Text.Json.JsonSerializer.Deserialize<VariableValue>(json);
            Assert.Equal(v.Type, back.Type);
            Assert.Equal(v, back);
        }
    }
}

public sealed class VariableStoreTests
{
    [Fact]
    public void Declared_type_is_enforced_on_assignment()
    {
        var s = new VariableStore();
        s.Declare("count", VariableType.Int, 0);
        s.Set("count", "7", VariableChangeSource.Network);
        Assert.Equal(VariableType.Int, s.Get("count").Type);
        Assert.Equal(7, s.Get("count").AsInt);
    }

    [Fact]
    public void Changed_fires_only_on_real_change()
    {
        var s = new VariableStore();
        var changes = new List<VariableChange>();
        s.Changed += (_, c) => changes.Add(c);
        s.Set("a", 1);
        s.Set("a", 1);
        s.Set("a", 2);
        Assert.Equal(2, changes.Count);
        Assert.Null(changes[0].OldValue);
        Assert.Equal(1, changes[1].OldValue!.Value.AsInt);
    }

    [Fact]
    public void Scheme_scope_falls_through_to_global_and_writes_back()
    {
        var global = new VariableStore();
        global.Set("shared", 1);
        var local = new VariableStore(global);
        local.Set("mine", 5);

        Assert.Equal(1, local.Get("shared").AsInt);
        local.Set("shared", 9);

        Assert.Equal(9, global.Get("shared").AsInt);
        Assert.False(global.Contains("mine"));
        Assert.DoesNotContain(local.Snapshot(), kv => kv.Key == "shared");
    }

    [Fact]
    public void Clear_resets_declared_and_removes_others()
    {
        var s = new VariableStore();
        s.Declare("n", VariableType.Int, 5);
        s.Set("n", 10);
        s.Set("tmp", "x");
        s.Clear();
        Assert.Equal(0, s.Get("n").AsInt);
        Assert.False(s.Contains("tmp"));
    }
}

public sealed class ExpressionTests
{
    private static VariableValue Eval(string expr, VariableStore? store = null, int seed = 1) =>
        Expression.Parse(expr).Evaluate(new StoreExpressionContext(store ?? new VariableStore(), random: new Random(seed)));

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("7 // 2", 3)]
    [InlineData("-7 // 2", -4)]
    [InlineData("7 % 3", 1)]
    [InlineData("8 / 2", 4)]
    [InlineData("-3 + 5", 2)]
    [InlineData("1_000 + 1", 1001)]
    [InlineData("abs(-5)", 5)]
    [InlineData("max(1, 9, 3)", 9)]
    [InlineData("min(4, 2)", 2)]
    [InlineData("round(2.5)", 3)]
    [InlineData("floor(2.9)", 2)]
    [InlineData("ceil(2.1)", 3)]
    [InlineData("clamp(15, 0, 10)", 10)]
    [InlineData("len('abc')", 3)]
    [InlineData("int('42')", 42)]
    public void Integer_results(string expr, long expected)
    {
        var v = Eval(expr);
        Assert.Equal(expected, v.AsInt);
        Assert.NotEqual(VariableType.String, v.Type);
    }

    [Fact]
    public void Slash_on_uneven_ints_yields_double()
    {
        Assert.Equal(3.5, Eval("7 / 2").AsDouble);
        Assert.Equal(VariableType.Double, Eval("1.5 * 2").Type);
    }

    [Theory]
    [InlineData("1 < 2", true)]
    [InlineData("2 <= 2", true)]
    [InlineData("3 > 4", false)]
    [InlineData("3 == 3.0", true)]
    [InlineData("'a' == \"a\"", true)]
    [InlineData("'a' != 'b'", true)]
    [InlineData("true && false", false)]
    [InlineData("true and not false", true)]
    [InlineData("false || 1 == 1", true)]
    [InlineData("!(1 == 2)", true)]
    [InlineData("1 != 2 && 2 != 3", true)]
    public void Boolean_results(string expr, bool expected) => Assert.Equal(expected, Eval(expr).AsBool);

    [Fact]
    public void Variables_are_read_from_store_including_dotted_names()
    {
        var s = new VariableStore();
        s.Set("count", 3);
        s.Set("state", "battle");
        Assert.True(Eval("count >= 3 && state == \"battle\"", s).AsBool);

        var ctx = new StoreExpressionContext(s, new Dictionary<string, VariableValue> { ["$match.x"] = 120, ["$match.y"] = 80 });
        Assert.Equal(200, Expression.Parse("$match.x + $match.y").Evaluate(ctx).AsInt);
    }

    [Fact]
    public void Undefined_variable_is_zero_unless_strict()
    {
        Assert.Equal(1, Eval("missing + 1").AsInt);
        var strict = Expression.Parse("missing + 1", strictVariables: true);
        var ex = Assert.Throws<ExpressionException>(() => strict.Evaluate(new StoreExpressionContext(new VariableStore())));
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defined_checks_existence()
    {
        var s = new VariableStore();
        s.Set("x", 0);
        Assert.True(Eval("defined(x)", s).AsBool);
        Assert.False(Eval("defined(y)", s).AsBool);
    }

    [Fact]
    public void String_concatenation()
    {
        var s = new VariableStore();
        s.Set("n", 2);
        Assert.Equal("round 2", Eval("'round ' + n", s).AsString);
    }

    [Fact]
    public void Short_circuit_avoids_errors()
    {
        Assert.False(Eval("false && (1 // 0 == 1)").AsBool);
        Assert.True(Eval("true || (1 // 0 == 1)").AsBool);
    }

    [Fact]
    public void Rand_is_inclusive_and_seeded()
    {
        var seen = new HashSet<long>();
        for (var seed = 0; seed < 200; seed++)
        {
            var v = Eval("rand(1, 3)", seed: seed).AsInt;
            Assert.InRange(v, 1, 3);
            seen.Add(v);
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(Eval("rand(1, 100)", seed: 5).AsInt, Eval("rand(1, 100)", seed: 5).AsInt);
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("'unterminated")]
    [InlineData("1 2")]
    [InlineData("@")]
    public void Syntax_errors_are_reported_with_position(string expr)
    {
        Assert.False(Expression.TryParse(expr, out _, out var err));
        Assert.Contains("at ", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1 // 0")]
    [InlineData("5 % 0")]
    [InlineData("'abc' * 2")]
    [InlineData("nosuch(1)")]
    [InlineData("abs(1, 2)")]
    public void Evaluation_errors_throw(string expr) => Assert.Throws<ExpressionException>(() => Eval(expr));

    [Fact]
    public void Referenced_variables_are_listed()
    {
        var e = Expression.Parse("a + b * a > max(c, 1)");
        Assert.Equal(new[] { "a", "b", "c" }, e.Variables);
    }
}
