"""主题（深色/浅色）相关的回归测试 —— 对应 GitHub issue #1

issue #1：Windows 11 深色主题下，Qt Fusion 风格给出的深色调色板让文字变白，
而界面样式表把背景写死成浅色，导致"白底白字"看不清。

本测试在离屏（offscreen）模式下真实构建主窗口，检查：
    1. 浅色模式下即使系统是深色，调色板也必须是浅色的；
    2. 深色模式下所有控件的样式表里不再出现写死的浅色；
    3. 所有"文字色 / 背景色"组合都满足 WCAG AA 对比度；
    4. 运行时切换主题能真正刷新控件样式。

运行：
    QT_QPA_PLATFORM=offscreen python -m unittest tests.test_theme -v
（Linux/macOS 上会自动为 win32* 模块打桩，以便导入 Windows 专用代码）
"""

import os
import re
import sys
import types
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")


def _stub_win32_modules():
    """在非 Windows 平台上为 win32* 模块打桩，使 Windows 专用代码可被导入"""
    if sys.platform.startswith("win"):
        return
    for name in ("win32gui", "win32ui", "win32con", "win32api", "win32process",
                 "win32clipboard", "pywintypes", "winerror"):
        if name in sys.modules:
            continue
        module = types.ModuleType(name)
        module.__getattr__ = lambda attr: 0  # 任意常量都返回 0
        sys.modules[name] = module


_stub_win32_modules()

from PyQt6.QtCore import QEvent, QObject, pyqtSignal  # noqa: E402
from PyQt6.QtGui import QPalette  # noqa: E402
from PyQt6.QtWidgets import QApplication, QWidget  # noqa: E402

from utils.theme import (  # noqa: E402
    DARK_TOKENS,
    LIGHT_TOKENS,
    MODE_AUTO,
    MODE_DARK,
    MODE_LIGHT,
    contrast_ratio,
    theme,
)


class _StubScrcpy(QObject):
    started = pyqtSignal()
    stopped = pyqtSignal()
    error = pyqtSignal(str)
    log = pyqtSignal(str)


class _StubDeviceMonitor(QObject):
    log_message = pyqtSignal(str)
    error_occurred = pyqtSignal(str)


class _StubController(QObject):
    action_recorded = pyqtSignal(dict)

    def __init__(self):
        super().__init__()
        self.device_monitor = _StubDeviceMonitor()

    def __getattr__(self, name):
        """主窗口会把按钮信号连到控制器的各种方法上，这里统一给空实现"""
        def _noop(*_args, **_kwargs):
            return None
        return _noop


class _StubAdb:
    def __init__(self):
        self.root_click_method = "su_input"


#: 保持对 QApplication 的强引用，避免它被垃圾回收后连带销毁 Qt 对象
_APP = None


def _app():
    global _APP
    _APP = QApplication.instance() or QApplication([])
    _APP.setStyle("Fusion")
    return _APP


def _build_main_window():
    from utils.config import config
    from gui.main_window import MainWindow

    theme.bind_config(config)
    window = MainWindow(config, _StubAdb(), _StubScrcpy(), _StubController())
    return window


# 只允许出现在"画在截图上"的覆盖层里的颜色（不属于界面主题）
_CANVAS_COLORS = {"#4caf50", "#ffffff", "#000000"}

def _theme_color_set():
    """当前主题允许出现的颜色集合（小写）"""
    return {value.lower() for value in theme.colors.values()}


def _dispose(window):
    """释放窗口

    不能调用 ``close()``：MainWindow.closeEvent 会弹出模态的退出确认框，
    在离屏测试里会永久阻塞。
    """
    window.hide()
    window.deleteLater()
    # 没有事件循环，需要手动处理 DeferredDelete，否则窗口会一直堆积
    QApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)


def _iter_widgets(window):
    yield window
    for child in window.findChildren(QWidget):
        yield child


def _color_declarations(stylesheet):
    """从样式表中抽取 (前景色, 背景色) 组合

    以 ``{}`` 块为单位解析：块内同时出现 color 与 background-color 时成对比较，
    只有 color 时与窗口背景色比较。
    """
    pairs = []
    for body in re.findall(r"\{([^{}]*)\}", stylesheet):
        fg = re.search(r"(?<![-\w])color\s*:\s*(#[0-9a-fA-F]{3,6})", body)
        bg = re.search(r"background-color\s*:\s*(#[0-9a-fA-F]{3,6})", body)
        if fg:
            pairs.append((fg.group(1), bg.group(1) if bg else None))
    return pairs


class ThemeTokensTest(unittest.TestCase):
    def test_token_tables_have_identical_keys(self):
        self.assertEqual(set(LIGHT_TOKENS), set(DARK_TOKENS))

    def test_light_and_dark_differ(self):
        self.assertNotEqual(LIGHT_TOKENS["window_bg"], DARK_TOKENS["window_bg"])

    def test_accent_text_is_white_in_both_themes(self):
        # 彩色按钮（成功/警告/错误/主色）上的文字，两种主题都必须是白色
        self.assertEqual(LIGHT_TOKENS["accent_text"], "#ffffff")
        self.assertEqual(DARK_TOKENS["accent_text"], "#ffffff")

    def test_contrast_of_core_pairs(self):
        """核心"文字/背景"组合必须满足 WCAG AA（>= 4.5）"""
        for name, tokens in (("light", LIGHT_TOKENS), ("dark", DARK_TOKENS)):
            with self.subTest(theme=name):
                for fg, bg in (
                    ("text", "window_bg"),
                    ("text", "surface_bg"),
                    ("text", "input_bg"),
                    ("text_strong", "window_bg"),
                    ("text_secondary", "window_bg"),
                    ("text_secondary", "surface_muted"),
                    ("text_secondary", "surface_alt"),
                    ("text_muted", "window_bg"),
                    ("success_text", "window_bg"),
                    ("warning_text", "window_bg"),
                    ("statusbar_text", "statusbar_bg"),
                    ("log_text", "log_bg"),
                    ("log_success", "log_bg"),
                    ("log_warning", "log_bg"),
                    ("log_error", "log_bg"),
                    ("accent_text", "accent"),
                    ("accent_text", "success"),
                    ("accent_text", "warning"),
                    ("accent_text", "error"),
                    ("accent_text", "neutral"),
                    ("selection_text", "selection_bg"),
                ):
                    ratio = contrast_ratio(tokens[fg], tokens[bg])
                    self.assertGreaterEqual(
                        ratio, 4.5,
                        f"{name}: {fg}({tokens[fg]}) on {bg}({tokens[bg]}) = {ratio:.2f}",
                    )


class ThemeManagerTest(unittest.TestCase):
    def setUp(self):
        self.app = _app()
        self._original_mode = theme.mode
        self._original_system = theme._system_dark

    def tearDown(self):
        theme._system_dark = self._original_system
        theme._mode = self._original_mode
        theme.apply(self.app)

    def test_light_mode_forces_light_palette_on_dark_system(self):
        """issue #1 的核心：系统深色 + 用户选浅色 => 必须是浅色调色板"""
        theme._system_dark = True
        theme.set_mode(MODE_LIGHT, self.app)

        self.assertFalse(theme.is_dark)
        window_color = self.app.palette().color(QPalette.ColorRole.Window).name()
        text_color = self.app.palette().color(QPalette.ColorRole.WindowText).name()
        self.assertEqual(window_color, LIGHT_TOKENS["window_bg"])
        self.assertEqual(text_color, LIGHT_TOKENS["text"])
        self.assertGreater(contrast_ratio(text_color, window_color), 7)

    def test_dark_mode_uses_dark_palette(self):
        theme.set_mode(MODE_DARK, self.app)
        self.assertTrue(theme.is_dark)
        self.assertEqual(
            self.app.palette().color(QPalette.ColorRole.Window).name(),
            DARK_TOKENS["window_bg"],
        )
        self.assertEqual(
            self.app.palette().color(QPalette.ColorRole.Base).name(),
            DARK_TOKENS["input_bg"],
        )

    def test_auto_mode_follows_system(self):
        theme._system_dark = True
        theme.set_mode(MODE_AUTO, self.app)
        self.assertTrue(theme.is_dark)

        theme._system_dark = False
        theme.apply(self.app)
        self.assertFalse(theme.is_dark)

    def test_stylesheet_only_for_dark_mode(self):
        theme.set_mode(MODE_LIGHT, self.app)
        self.assertEqual(theme.stylesheet(), "")
        theme.set_mode(MODE_DARK, self.app)
        self.assertIn("QScrollBar", theme.stylesheet())
        self.assertEqual(self.app.styleSheet(), theme.stylesheet())

    def test_unknown_color_token_raises(self):
        with self.assertRaises(KeyError):
            theme.color("no_such_token")


class MainWindowThemeTest(unittest.TestCase):
    """真实构建主窗口，检查每个控件的配色"""

    @classmethod
    def setUpClass(cls):
        cls.app = _app()

    def setUp(self):
        self._original_mode = theme.mode

    def tearDown(self):
        theme._mode = self._original_mode
        theme.apply(self.app)

    def _build(self, mode):
        theme.set_mode(mode, self.app)
        window = _build_main_window()
        window.show()
        self.app.processEvents()
        return window

    def test_no_colors_outside_theme_tokens(self):
        """任何控件样式表里的颜色都必须来自当前主题的令牌

        这是 issue #1 的根因检查：一旦有人再写死 #f5f5f5 / white 之类的颜色，
        深色主题下就会重新出现"白底白字"。
        """
        for mode in (MODE_DARK, MODE_LIGHT):
            with self.subTest(theme=mode):
                window = self._build(mode)
                allowed = _theme_color_set() | _CANVAS_COLORS
                offenders = []
                for widget in _iter_widgets(window):
                    sheet = widget.styleSheet().lower()
                    for color in re.findall(r"#[0-9a-fA-F]{3,6}", sheet):
                        if color.lower() not in allowed:
                            offenders.append((type(widget).__name__, color))
                self.assertEqual(
                    offenders, [], f"样式表中出现了不属于主题的颜色: {offenders}"
                )
                _dispose(window)

    def test_all_widget_text_colors_are_readable(self):
        """所有样式表里显式声明的文字色，都必须与其背景有足够对比度"""
        for mode in (MODE_LIGHT, MODE_DARK):
            with self.subTest(theme=mode):
                window = self._build(mode)
                window_bg = theme.color("window_bg")
                problems = []
                for widget in _iter_widgets(window):
                    for fg, bg in _color_declarations(widget.styleSheet()):
                        ratio = contrast_ratio(fg, bg or window_bg)
                        if ratio < 4.5:
                            problems.append(
                                f"{type(widget).__name__}: {fg} on {bg or window_bg} = {ratio:.2f}"
                            )
                self.assertEqual(problems, [])
                _dispose(window)

    def test_switching_theme_live_updates_widgets(self):
        window = self._build(MODE_LIGHT)
        light_label_style = window.right_panel.device_coord_label.styleSheet()
        light_panel_style = window.left_panel.styleSheet()
        self.assertIn(LIGHT_TOKENS["text_strong"], light_label_style)

        # 模拟系统从浅色切到深色（auto 模式下自动跟随）
        theme._system_dark = True
        theme.set_mode(MODE_AUTO, self.app)
        self.app.processEvents()

        self.assertTrue(theme.is_dark)
        self.assertNotEqual(
            window.right_panel.device_coord_label.styleSheet(), light_label_style
        )
        self.assertIn(
            DARK_TOKENS["text_strong"],
            window.right_panel.device_coord_label.styleSheet(),
        )
        self.assertIn(DARK_TOKENS["border"], window.left_panel.styleSheet())
        self.assertNotEqual(window.left_panel.styleSheet(), light_panel_style)

        _dispose(window)

    def test_log_uses_console_colors(self):
        window = self._build(MODE_DARK)
        window.log("测试日志", "info")
        window.log("成功日志", "success")
        window.log("错误日志", "error")
        html = window.log_text.toHtml()
        self.assertIn("测试日志", html)
        self.assertIn(DARK_TOKENS["log_success"], html.lower())
        self.assertIn(DARK_TOKENS["log_error"], html.lower())
        _dispose(window)

    def test_monitor_status_helper(self):
        window = self._build(MODE_DARK)
        window.center_panel.set_monitor_status("状态: 监控中...", "running")
        self.assertIn(
            DARK_TOKENS["success_text"],
            window.center_panel.monitor_status_label.styleSheet(),
        )
        window.center_panel.set_monitor_status("状态: 已停止", "idle")
        self.assertIn(
            DARK_TOKENS["text_secondary"],
            window.center_panel.monitor_status_label.styleSheet(),
        )
        _dispose(window)


class MainEntryTest(unittest.TestCase):
    """程序入口（main.py）必须在创建任何窗口之前就应用主题"""

    def test_app_init_applies_theme(self):
        import main
        from utils.config import config

        config.set("theme_mode", MODE_DARK)
        try:
            boot = main.PhoneControllerApp()
        finally:
            config.set("theme_mode", MODE_AUTO)

        self.assertTrue(theme.is_dark)
        self.assertEqual(
            boot.app.palette().color(QPalette.ColorRole.Window).name(),
            DARK_TOKENS["window_bg"],
        )
        self.assertEqual(boot.app.styleSheet(), theme.stylesheet())

    def test_watch_system_connects_when_supported(self):
        app = _app()
        hints = app.styleHints()
        if not hasattr(hints, "colorSchemeChanged"):
            self.skipTest("当前 Qt 版本不支持 colorSchemeChanged")
        self.assertTrue(theme.watch_system(app))


class SettingsDialogThemeTest(unittest.TestCase):
    def setUp(self):
        self.app = _app()
        self._original_mode = theme.mode

    def tearDown(self):
        theme._mode = self._original_mode
        theme.apply(self.app)

    def test_theme_selector_exists_and_switches(self):
        from gui.settings_dialog import SettingsDialog

        theme.set_mode(MODE_LIGHT, self.app)
        dialog = SettingsDialog()
        self.assertEqual(dialog.theme_combo.currentData(), MODE_LIGHT)

        index = dialog.theme_combo.findData(MODE_DARK)
        dialog.theme_combo.setCurrentIndex(index)
        self.app.processEvents()

        self.assertTrue(theme.is_dark)
        self.assertEqual(dialog.theme_combo.currentData(), MODE_DARK)
        dialog.deleteLater()


if __name__ == "__main__":
    unittest.main(verbosity=2)
