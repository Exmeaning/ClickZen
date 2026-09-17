"""应用主题管理（深色 / 浅色模式）

背景（GitHub issue #1）：
    在 Windows 11 深色主题下，Qt 的 Fusion 风格会跟随系统使用深色调色板，
    于是控件文字变成白色；而界面里大量样式表把背景写死成了浅色
    （#f5f5f5 / white / #fafafa ...），结果就是"白底白字"，完全看不清。

本模块做的事：
    1. 提供一套语义化颜色令牌（token），浅色 / 深色各一份；
    2. 依据「跟随系统 / 浅色 / 深色」的设置解析出最终主题，并生成匹配的
       QPalette（浅色模式下强制使用浅色调色板，避免系统深色主题污染文字颜色）；
    3. 深色模式下额外提供一份全局 QSS，让所有控件（输入框、列表、下拉框、
       菜单、滚动条、标签页……）都有一致的深色外观；
    4. 支持运行时切换主题：所有面板把自己的样式表刷新函数注册进来，
       主题变化时统一重新应用。

用法：
    from utils.theme import theme

    label.setStyleSheet(f"color: {theme.color('text_secondary')}; font-size: 11px;")

    # 长期存活的窗口/面板注册刷新回调，切换主题时自动重绘样式
    theme.register(self.apply_theme)
"""

import inspect
import weakref

from PyQt6.QtCore import QSettings, Qt
from PyQt6.QtGui import QColor, QPalette
from PyQt6.QtWidgets import QApplication


# 主题模式
MODE_AUTO = "auto"
MODE_LIGHT = "light"
MODE_DARK = "dark"

MODES = (MODE_AUTO, MODE_LIGHT, MODE_DARK)

# 模式对应的中文名称（设置界面使用）
MODE_LABELS = {
    MODE_AUTO: "跟随系统",
    MODE_LIGHT: "浅色",
    MODE_DARK: "深色",
}


# ---------------------------------------------------------------------------
# 颜色令牌
# ---------------------------------------------------------------------------
# 命名规则：
#   window_bg / surface_*  背景层次
#   border_*               边框
#   text*                  文字
#   accent / success / warning / error   语义色（*_soft_bg 为对应的浅色底）
#   log_*                  日志控制台
# ---------------------------------------------------------------------------

LIGHT_TOKENS = {
    # 背景
    "window_bg": "#f5f5f5",
    "surface_bg": "#ffffff",
    "surface_alt": "#fafafa",
    "surface_muted": "#f0f0f0",
    "input_bg": "#ffffff",
    "input_bg_focus": "#f8f8f8",
    "canvas_bg": "#333333",
    # 边框
    "border": "#e0e0e0",
    "border_subtle": "#f0f0f0",
    "border_strong": "#9E9E9E",
    "border_focus": "#757575",
    # 文字
    "text": "#333333",
    "text_strong": "#424242",
    "text_secondary": "#666666",
    "text_muted": "#707070",
    "text_disabled": "#9e9e9e",
    "text_inverse": "#ffffff",
    # 彩色按钮（主色/成功/警告/错误）上的文字，两种主题都用白色
    "accent_text": "#ffffff",
    # 链接
    "link": "#616161",
    "link_accent": "#1976D2",
    # 选中 / 悬停
    "selection_bg": "#1976D2",
    "selection_text": "#ffffff",
    "hover_bg": "#F5F5F5",
    # 主色
    "accent": "#1976D2",
    "accent_hover": "#1565C0",
    "accent_soft_bg": "#e3f2fd",
    "accent_soft_text": "#1565C0",
    # 成功
    "success": "#2E7D32",
    "success_hover": "#388E3C",
    "success_text": "#2E7D32",
    "success_soft_bg": "#E8F5E9",
    # 警告
    "warning": "#C43E00",
    "warning_hover": "#A83500",
    "warning_text": "#C43E00",
    "warning_soft_bg": "#FFF8E1",
    "warning_soft_bg_alt": "#FFF3E0",
    # 错误
    "error": "#c62828",
    "error_hover": "#b71c1c",
    "error_text": "#c62828",
    # 中性按钮
    "neutral": "#757575",
    "neutral_hover": "#616161",
    "neutral_checked": "#424242",
    "disabled_bg": "#cccccc",
    # 状态栏
    "statusbar_bg": "#37474F",
    "statusbar_text": "#ffffff",
    # 日志控制台
    "log_bg": "#1e1e1e",
    "log_text": "#d4d4d4",
    "log_border": "#3c3c3c",
    "log_time": "#8a8a8a",
    "log_success": "#4ec9b0",
    "log_warning": "#ce9178",
    "log_error": "#f48771",
}

DARK_TOKENS = {
    # 背景
    "window_bg": "#1f1f1f",
    "surface_bg": "#2b2b2b",
    "surface_alt": "#262626",
    "surface_muted": "#333333",
    "input_bg": "#2b2b2b",
    "input_bg_focus": "#333333",
    "canvas_bg": "#2a2a2a",
    # 边框
    "border": "#3c3c3c",
    "border_subtle": "#383838",
    "border_strong": "#6a6a6a",
    "border_focus": "#9e9e9e",
    # 文字
    "text": "#e6e6e6",
    "text_strong": "#f5f5f5",
    "text_secondary": "#c4c4c4",
    "text_muted": "#a6a6a6",
    "text_disabled": "#6f6f6f",
    "text_inverse": "#1f1f1f",
    # 彩色按钮（主色/成功/警告/错误）上的文字，两种主题都用白色
    "accent_text": "#ffffff",
    # 链接
    "link": "#9ecbff",
    "link_accent": "#4aa3ff",
    # 选中 / 悬停
    "selection_bg": "#264f78",
    "selection_text": "#ffffff",
    "hover_bg": "#3a3a3a",
    # 主色
    "accent": "#0f6cbd",
    "accent_hover": "#1a7fd4",
    "accent_soft_bg": "#10283f",
    "accent_soft_text": "#90caf9",
    # 成功
    "success": "#2e7d32",
    "success_hover": "#388e3c",
    "success_text": "#81c784",
    "success_soft_bg": "#14301a",
    # 警告
    "warning": "#a05e00",
    "warning_hover": "#b26a00",
    "warning_text": "#ffb74d",
    "warning_soft_bg": "#33260f",
    "warning_soft_bg_alt": "#3d2d12",
    # 错误
    "error": "#c62828",
    "error_hover": "#d32f2f",
    "error_text": "#ef9a9a",
    # 中性按钮
    "neutral": "#4a4a4a",
    "neutral_hover": "#5a5a5a",
    "neutral_checked": "#3a3a3a",
    "disabled_bg": "#3a3a3a",
    # 状态栏
    "statusbar_bg": "#252526",
    "statusbar_text": "#e6e6e6",
    # 日志控制台
    "log_bg": "#1a1a1a",
    "log_text": "#d4d4d4",
    "log_border": "#3c3c3c",
    "log_time": "#8a8a8a",
    "log_success": "#4ec9b0",
    "log_warning": "#d7a86e",
    "log_error": "#f48771",
}

TOKENS = {
    "light": LIGHT_TOKENS,
    "dark": DARK_TOKENS,
}


def _hex_to_rgb(value):
    """#rgb / #rrggbb -> (r, g, b)"""
    value = value.lstrip("#")
    if len(value) == 3:
        value = "".join(ch * 2 for ch in value)
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def relative_luminance(hex_color):
    """WCAG 相对亮度，用于对比度检查"""
    def channel(v):
        v /= 255.0
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4

    r, g, b = _hex_to_rgb(hex_color)
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)


def contrast_ratio(fg_hex, bg_hex):
    """WCAG 对比度（1 ~ 21）"""
    l1 = relative_luminance(fg_hex)
    l2 = relative_luminance(bg_hex)
    lighter, darker = (l1, l2) if l1 >= l2 else (l2, l1)
    return (lighter + 0.05) / (darker + 0.05)


class ThemeManager:
    """主题管理器（单例：模块级 ``theme``）

    故意不继承 QObject：本模块会在 ``QApplication`` 创建之前就被导入，
    此时创建 QObject 并不安全（QApplication 销毁时可能被一并回收）。
    因此这里用普通的回调列表代替 pyqtSignal。
    """

    def __init__(self, config=None, mode=None):
        self._config = config
        self._mode = mode if mode in MODES else MODE_AUTO
        self._dark = False
        self._callbacks = []
        self._listeners = []
        self._system_dark = None  # 系统主题缓存，便于测试注入
        self._load_mode()

    # ------------------------------------------------------------------
    # 主题变化监听
    # ------------------------------------------------------------------
    def add_listener(self, callback):
        """注册主题变化监听器，回调参数为 ``is_dark``"""
        if callback not in self._listeners:
            self._listeners.append(callback)
        return callback

    def remove_listener(self, callback):
        if callback in self._listeners:
            self._listeners.remove(callback)

    def _notify_listeners(self):
        for callback in list(self._listeners):
            try:
                callback(self._dark)
            except Exception as exc:  # pragma: no cover - 防御性
                print(f"[theme] 主题监听器执行失败: {exc}")

    # ------------------------------------------------------------------
    # 配置读写
    # ------------------------------------------------------------------
    def bind_config(self, config):
        """绑定 utils.config.Config，用于持久化主题偏好"""
        self._config = config
        self._load_mode()

    def _load_mode(self):
        if self._config is None:
            return
        try:
            stored = self._config.get("theme_mode")
        except Exception:
            return
        if stored in MODES:
            self._mode = stored

    def _save_mode(self):
        if self._config is None:
            return
        try:
            self._config.set("theme_mode", self._mode)
        except Exception:
            pass

    # ------------------------------------------------------------------
    # 状态查询
    # ------------------------------------------------------------------
    @property
    def mode(self):
        """用户设置的模式：auto / light / dark"""
        return self._mode

    @property
    def is_dark(self):
        """当前生效的是否为深色主题"""
        return self._dark

    @property
    def colors(self):
        """当前主题的颜色令牌字典"""
        return TOKENS["dark" if self._dark else "light"]

    def color(self, name):
        """取当前主题下的某个颜色（十六进制字符串）"""
        colors = self.colors
        if name not in colors:
            raise KeyError(f"未知的主题颜色令牌: {name}")
        return colors[name]

    def qcolor(self, name):
        """取当前主题下的某个颜色（QColor）"""
        return QColor(self.color(name))

    def __getitem__(self, name):
        return self.color(name)

    # ------------------------------------------------------------------
    # 系统主题探测
    # ------------------------------------------------------------------
    def system_is_dark(self):
        """探测操作系统是否处于深色模式"""
        if self._system_dark is not None:
            return self._system_dark
        detected = self._detect_system_dark()
        return detected

    def _detect_system_dark(self):
        app = QApplication.instance()
        if app is not None:
            hints = app.styleHints()
            color_scheme = getattr(hints, "colorScheme", None)
            color_scheme_enum = getattr(Qt, "ColorScheme", None)
            if color_scheme is not None and color_scheme_enum is not None:
                try:
                    scheme = color_scheme()
                    if scheme == color_scheme_enum.Dark:
                        return True
                    if scheme == color_scheme_enum.Light:
                        return False
                except Exception:
                    pass

        # Qt < 6.5：Windows 直接读注册表
        try:
            import winreg  # 仅 Windows 可用

            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\Personalize",
            ) as key:
                value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
                return int(value) == 0
        except Exception:
            pass

        # 其它平台退回到桌面环境的配色设置
        try:
            settings = QSettings("org.kde.kdeglobals", "General")
            scheme = settings.value("ColorScheme", "")
            if scheme and "dark" in str(scheme).lower():
                return True
        except Exception:
            pass

        return False

    def _resolve_dark(self):
        if self._mode == MODE_DARK:
            return True
        if self._mode == MODE_LIGHT:
            return False
        return self.system_is_dark()

    # ------------------------------------------------------------------
    # 调色板 / 样式表
    # ------------------------------------------------------------------
    def palette(self, dark=None):
        """生成与主题匹配的 QPalette

        关键点：浅色模式下必须显式使用浅色调色板。否则在系统深色主题里，
        Fusion 风格会给出深色调色板（白色文字），而界面里浅色背景是写死的，
        就会出现 issue #1 描述的"白底白字"。
        """
        tokens = TOKENS["dark" if (self._dark if dark is None else dark) else "light"]
        palette = QPalette()

        def c(name):
            return QColor(tokens[name])

        role = QPalette.ColorRole
        palette.setColor(role.Window, c("window_bg"))
        palette.setColor(role.WindowText, c("text"))
        palette.setColor(role.Base, c("input_bg"))
        palette.setColor(role.AlternateBase, c("surface_alt"))
        palette.setColor(role.Text, c("text"))
        palette.setColor(role.Button, c("surface_bg"))
        palette.setColor(role.ButtonText, c("text"))
        palette.setColor(role.BrightText, c("text_strong"))
        palette.setColor(role.ToolTipBase, c("surface_bg"))
        palette.setColor(role.ToolTipText, c("text"))
        palette.setColor(role.PlaceholderText, QColor(tokens["text_muted"]))
        palette.setColor(role.Link, c("link_accent"))
        palette.setColor(role.LinkVisited, c("link"))
        palette.setColor(role.Highlight, c("selection_bg"))
        palette.setColor(role.HighlightedText, c("selection_text"))
        palette.setColor(role.Mid, c("border"))
        palette.setColor(role.Midlight, c("border_subtle"))
        palette.setColor(role.Dark, c("border"))
        palette.setColor(role.Light, c("surface_bg"))
        palette.setColor(role.Shadow, c("border"))

        group = QPalette.ColorGroup
        for grp in (group.Active, group.Inactive):
            palette.setColor(grp, role.PlaceholderText, c("text_muted"))

        palette.setColor(group.Disabled, role.WindowText, c("text_disabled"))
        palette.setColor(group.Disabled, role.Text, c("text_disabled"))
        palette.setColor(group.Disabled, role.ButtonText, c("text_disabled"))
        palette.setColor(group.Disabled, role.HighlightedText, c("text_disabled"))
        palette.setColor(group.Disabled, role.Highlight, c("surface_muted"))
        return palette

    def stylesheet(self, dark=None):
        """全局样式表

        浅色模式返回空字符串——保持原有观感完全不变（也避免和控件级样式表打架）；
        深色模式返回一份完整的深色 QSS。
        """
        is_dark = self._dark if dark is None else dark
        if not is_dark:
            return ""
        return _build_dark_stylesheet(TOKENS["dark"])

    # ------------------------------------------------------------------
    # 应用 / 切换
    # ------------------------------------------------------------------
    def apply(self, app=None):
        """把当前主题应用到 QApplication"""
        app = app or QApplication.instance()
        self._dark = self._resolve_dark()

        if app is not None:
            # Fusion 风格 + 显式调色板，确保文字颜色与背景永远成套
            app.setStyle("Fusion")
            app.setPalette(self.palette())
            app.setStyleSheet(self.stylesheet())

        self._apply_callbacks()
        self._notify_listeners()
        return self._dark

    def set_mode(self, mode, app=None):
        """设置主题模式（auto / light / dark）并立即生效"""
        if mode not in MODES:
            raise ValueError(f"未知的主题模式: {mode}（可选 {', '.join(MODES)}）")
        changed = mode != self._mode
        was_dark = self._dark
        self._mode = mode
        self._save_mode()
        self.apply(app)
        return changed or was_dark != self._dark

    def watch_system(self, app=None):
        """监听系统主题变化（Qt 6.5+ 提供 colorSchemeChanged）"""
        app = app or QApplication.instance()
        if app is None:
            return False
        hints = app.styleHints()
        signal = getattr(hints, "colorSchemeChanged", None)
        if signal is None:
            return False
        try:
            signal.connect(self._on_system_scheme_changed)
            return True
        except (TypeError, AttributeError):
            return False

    def _on_system_scheme_changed(self, *_args):
        if self._mode == MODE_AUTO:
            self.apply()

    # ------------------------------------------------------------------
    # 控件样式刷新回调
    # ------------------------------------------------------------------
    def register(self, callback):
        """注册"主题变化时需要重新执行的样式刷新函数"

        使用弱引用保存，控件销毁后自动清理；注册时会立即执行一次，
        因此可以直接用来完成初始化时的样式设置。
        """
        if inspect.ismethod(callback):
            ref = weakref.WeakMethod(callback)
        else:
            ref = weakref.ref(callback)
        self._callbacks.append(ref)
        callback()
        return callback

    def unregister(self, callback):
        for ref in list(self._callbacks):
            target = ref()
            if target is callback or (target is not None and target == callback):
                self._callbacks.remove(ref)

    def _apply_callbacks(self):
        alive = []
        for ref in self._callbacks:
            callback = ref()
            if callback is None:
                continue
            alive.append(ref)
            try:
                callback()
            except RuntimeError:
                # 绑定的 C++ 对象已被销毁
                continue
            except Exception as exc:  # pragma: no cover - 防御性
                print(f"[theme] 刷新控件样式失败: {exc}")
        self._callbacks = alive


def _build_dark_stylesheet(t):
    """根据深色令牌生成全局 QSS"""
    return f"""
/* ---- 窗口与容器 ---- */
QMainWindow, QDialog, QMessageBox, QWizard, QWidget {{
    background-color: {t['window_bg']};
    color: {t['text']};
    selection-background-color: {t['selection_bg']};
    selection-color: {t['selection_text']};
}}
QWidget:disabled {{
    color: {t['text_disabled']};
}}
QFrame {{
    color: {t['text']};
}}
QSplitter::handle {{
    background-color: {t['border']};
}}

QToolTip {{
    background-color: {t['surface_bg']};
    color: {t['text']};
    border: 1px solid {t['border_strong']};
    padding: 4px;
}}

/* ---- 输入 / 视图类控件 ---- */
QLineEdit, QTextEdit, QPlainTextEdit, QTextBrowser, QSpinBox, QDoubleSpinBox,
QComboBox, QListWidget, QListView, QTreeWidget, QTreeView,
QTableWidget, QTableView {{
    background-color: {t['input_bg']};
    alternate-background-color: {t['surface_alt']};
    color: {t['text']};
    border: 1px solid {t['border_strong']};
    border-radius: 4px;
    selection-background-color: {t['selection_bg']};
    selection-color: {t['selection_text']};
}}
QLineEdit, QSpinBox, QDoubleSpinBox, QComboBox {{
    padding: 3px 4px;
}}
QLineEdit:focus, QTextEdit:focus, QPlainTextEdit:focus, QSpinBox:focus,
QDoubleSpinBox:focus, QComboBox:focus {{
    border-color: {t['border_focus']};
    background-color: {t['input_bg_focus']};
}}
QLineEdit:disabled, QTextEdit:disabled, QSpinBox:disabled,
QDoubleSpinBox:disabled, QComboBox:disabled {{
    color: {t['text_disabled']};
    background-color: {t['surface_muted']};
}}

QComboBox QAbstractItemView {{
    background-color: {t['surface_bg']};
    color: {t['text']};
    border: 1px solid {t['border_strong']};
    selection-background-color: {t['selection_bg']};
    selection-color: {t['selection_text']};
    outline: none;
}}

QListWidget::item, QListView::item {{
    color: {t['text']};
}}
QListWidget::item:selected, QListView::item:selected,
QTreeWidget::item:selected, QTableWidget::item:selected {{
    background-color: {t['selection_bg']};
    color: {t['selection_text']};
}}

QHeaderView::section {{
    background-color: {t['surface_muted']};
    color: {t['text']};
    border: none;
    border-right: 1px solid {t['border']};
    border-bottom: 1px solid {t['border']};
    padding: 4px;
}}

/* ---- 按钮 ---- */
QPushButton {{
    background-color: {t['surface_bg']};
    color: {t['text']};
    border: 1px solid {t['border_strong']};
    border-radius: 4px;
    padding: 4px 8px;
}}
QPushButton:hover {{
    background-color: {t['hover_bg']};
    border-color: {t['border_focus']};
}}
QPushButton:pressed {{
    background-color: {t['surface_muted']};
}}
QPushButton:checked {{
    background-color: {t['neutral_checked']};
    color: {t['text']};
}}
QPushButton:disabled {{
    background-color: {t['surface_muted']};
    color: {t['text_disabled']};
    border-color: {t['border']};
}}
QToolButton {{
    background-color: transparent;
    color: {t['text']};
    border: 1px solid transparent;
    border-radius: 4px;
    padding: 3px;
}}
QToolButton:hover {{
    background-color: {t['hover_bg']};
    border-color: {t['border']};
}}

/* ---- 复选 / 单选 / 分组 ---- */
QCheckBox, QRadioButton {{
    background: transparent;
    color: {t['text']};
    spacing: 6px;
}}
QCheckBox:disabled, QRadioButton:disabled {{
    color: {t['text_disabled']};
}}

QGroupBox {{
    color: {t['text']};
    border: 1px solid {t['border']};
    border-radius: 6px;
    margin-top: 8px;
    padding-top: 8px;
}}
QGroupBox::title {{
    subcontrol-origin: margin;
    left: 8px;
    padding: 0 6px;
    color: {t['text_strong']};
}}

/* ---- 标签页 ---- */
QTabWidget::pane {{
    border: 1px solid {t['border']};
    background-color: {t['window_bg']};
}}
QTabBar::tab {{
    background-color: {t['surface_muted']};
    color: {t['text_secondary']};
    border: 1px solid {t['border']};
    border-bottom: none;
    border-top-left-radius: 4px;
    border-top-right-radius: 4px;
    padding: 6px 14px;
}}
QTabBar::tab:selected {{
    background-color: {t['window_bg']};
    color: {t['text']};
}}
QTabBar::tab:hover {{
    background-color: {t['hover_bg']};
}}

/* ---- 菜单 ---- */
QMenuBar {{
    background-color: {t['window_bg']};
    color: {t['text']};
}}
QMenuBar::item:selected {{
    background-color: {t['hover_bg']};
}}
QMenu {{
    background-color: {t['surface_bg']};
    color: {t['text']};
    border: 1px solid {t['border_strong']};
}}
QMenu::item:selected {{
    background-color: {t['selection_bg']};
    color: {t['selection_text']};
}}
QMenu::separator {{
    height: 1px;
    background-color: {t['border']};
    margin: 4px 8px;
}}

/* ---- 状态栏 ---- */
QStatusBar {{
    background-color: {t['statusbar_bg']};
    color: {t['statusbar_text']};
}}
QStatusBar::item {{
    border: none;
}}
QStatusBar QLabel {{
    background: transparent;
    color: {t['statusbar_text']};
}}

/* ---- 滚动条 ---- */
QScrollBar:vertical {{
    background-color: {t['window_bg']};
    width: 12px;
    margin: 0;
}}
QScrollBar::handle:vertical {{
    background-color: {t['border_strong']};
    min-height: 24px;
    border-radius: 5px;
    margin: 2px;
}}
QScrollBar:horizontal {{
    background-color: {t['window_bg']};
    height: 12px;
    margin: 0;
}}
QScrollBar::handle:horizontal {{
    background-color: {t['border_strong']};
    min-width: 24px;
    border-radius: 5px;
    margin: 2px;
}}
QScrollBar::handle:vertical:hover, QScrollBar::handle:horizontal:hover {{
    background-color: {t['border_focus']};
}}
QScrollBar::add-line, QScrollBar::sub-line {{
    width: 0;
    height: 0;
}}
QScrollBar::add-page, QScrollBar::sub-page {{
    background: transparent;
}}

/* ---- 进度条 / 滑块 ---- */
QProgressBar {{
    background-color: {t['input_bg']};
    color: {t['text']};
    border: 1px solid {t['border_strong']};
    border-radius: 4px;
    text-align: center;
}}
QProgressBar::chunk {{
    background-color: {t['accent']};
}}
QSlider::groove:horizontal {{
    height: 4px;
    background-color: {t['border']};
    border-radius: 2px;
}}
QSlider::handle:horizontal {{
    background-color: {t['accent']};
    width: 14px;
    margin: -5px 0;
    border-radius: 7px;
}}
"""


#: 全局主题管理器
theme = ThemeManager()
