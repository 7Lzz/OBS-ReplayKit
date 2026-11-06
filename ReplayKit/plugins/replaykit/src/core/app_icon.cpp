// live app icon swap and taskbar identity
#include "prelude.hpp"
#include "core/app_icon.hpp"

#include <QCryptographicHash>
#include <QJsonObject>

#include "app/clip_buffer.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"

namespace rk {

namespace {

// icon slot and taskbar tag state for the clips window; settings and setup keep theirs in exported slots because their window modules hand them to ApplyAuxWindowIcon when a fresh window is built
HICON g_ownedIconClips = nullptr;
int g_taggedClips = -1;

// -- live app-icon swap (helper Appearance tab) -- SETICON <path> over the ipc pipe. covers obss title bar, taskbar button and system-tray icon; "-" restores what obs shipped with (captured on first use). SETICONDOT toggles the red recording dot drawn on the tray icon, and only there, while a recording / replay buffer is running.
//
// win11 taskbar note: qt6 setWindowIcon (WM_SETICON at 16/32px) updates the title bar + alt-tab but NOT the
// win11 taskbar button -- win11 only picks up an icon change when the WM_SETICON payload is large, and it also
// reads the window CLASS icon. so we push one 256px HICON to every WM_SETICON slot AND the class, and re-assert
// on Show/WindowStateChange (restoring from the tray rebuilds the taskbar button).
bool g_appIconDefaultsCaptured = false;
QIcon g_appIconDefaultMain;
// the tray icons obs puts on the tray itself, one per state (plain, something is on, recording paused), kept as obs set them
QIcon g_obsTrayIcon[3];
// the icon on the tray is one of ours, put there for a custom icon. with the default icon the tray belongs to obs and follows what obs is doing
bool g_trayIsOurs = false;
qint64 g_lastTrayIconKey = 0;
HICON g_classOrigBig = nullptr;   // obs's own class icons, captured once
HICON g_classOrigSmall = nullptr;
HICON g_ownedIcon = nullptr;      // the single 256px HICON we push to every slot + the class; freed on the next change
QString g_appIconPath;            // "" / "-" == default; last value the helper sent
int g_taskbarTaggedCustom = -1;   // -1 unknown / 0 default / 1 custom -- last state we retagged the obs taskbar button for

// QIcon -> HICON at a given px size (works for .ico and .png). caller owns the result (DestroyIcon).
HICON HIconFromQIcon(const QIcon &icon, int size)
{
	if (icon.isNull() || size <= 0)
		return nullptr;
	QImage img = icon.pixmap(size, size).toImage().convertToFormat(QImage::Format_ARGB32_Premultiplied);
	if (img.isNull())
		return nullptr;

	BITMAPV5HEADER bi = {};
	bi.bV5Size = sizeof(BITMAPV5HEADER);
	bi.bV5Width = img.width();
	bi.bV5Height = -img.height(); // top-down
	bi.bV5Planes = 1;
	bi.bV5BitCount = 32;
	bi.bV5Compression = BI_BITFIELDS;
	bi.bV5RedMask = 0x00FF0000;
	bi.bV5GreenMask = 0x0000FF00;
	bi.bV5BlueMask = 0x000000FF;
	bi.bV5AlphaMask = 0xFF000000;

	HDC hdc = GetDC(nullptr);
	void *bits = nullptr;
	HBITMAP color = CreateDIBSection(hdc, (BITMAPINFO *)&bi, DIB_RGB_COLORS, &bits, nullptr, 0);
	ReleaseDC(nullptr, hdc);
	if (!color)
		return nullptr;
	for (int y = 0; y < img.height(); ++y)
		memcpy((quint8 *)bits + (size_t)y * img.width() * 4, img.constScanLine(y), (size_t)img.width() * 4);

	// all-zero AND mask -- the 32bpp alpha channel carries transparency; 1bpp rows are WORD-aligned.
	std::vector<quint8> maskBits((size_t)(((img.width() + 15) / 16) * 2) * img.height(), 0);
	HBITMAP mask = CreateBitmap(img.width(), img.height(), 1, 1, maskBits.data());
	ICONINFO ii = {};
	ii.fIcon = TRUE;
	ii.hbmColor = color;
	ii.hbmMask = mask;
	HICON hIcon = CreateIconIndirect(&ii);
	DeleteObject(color);
	DeleteObject(mask);
	return hIcon;
}

// overlays the recording indicator -- geometry matched to obss own tray_active.png: a pure-red filled circle in the bottom-left, 37.5% of the icon, ~5% inset from the left and bottom edges, no ring.
QIcon ComposeRecordingDot(const QIcon &base)
{
	if (base.isNull())
		return base;
	QIcon out;
	for (int sz : {16, 20, 24, 32, 40, 48, 64, 128, 256}) {
		QPixmap pm = base.pixmap(sz, sz);
		if (pm.isNull())
			continue;
		qreal s = pm.width();
		qreal d = s * 0.375;
		QRectF dot(s * 0.047, s - s * 0.051 - d, d, d);
		QPainter p(&pm);
		p.setRenderHint(QPainter::Antialiasing, true);
		p.setPen(Qt::NoPen);
		p.setBrush(QColor(255, 0, 0));
		p.drawEllipse(dot);
		p.end();
		out.addPixmap(pm);
	}
	return out.isNull() ? base : out;
}

// how many pixels of an icon are the red of the recording dot: what tells the icon obs shows while something is on from the plain one
int RedDotPixels(const QIcon &icon)
{
	if (icon.isNull())
		return 0;
	const QImage img = icon.pixmap(32, 32).toImage().convertToFormat(QImage::Format_ARGB32);
	int count = 0;
	for (int y = 0; y < img.height(); ++y) {
		const QRgb *row = reinterpret_cast<const QRgb *>(img.constScanLine(y));
		for (int x = 0; x < img.width(); ++x) {
			if (qAlpha(row[x]) > 128 && qRed(row[x]) > 200 && qGreen(row[x]) < 70 && qBlue(row[x]) < 70)
				++count;
		}
	}
	return count;
}

constexpr int kRedDotPixels = 20;

// the three looks obs gives its tray icon: plain, with the red dot while a recording, the replay buffer, a stream or the virtual camera is on, and paused
int ObsTrayState()
{
	if (obs_frontend_recording_active() && obs_frontend_recording_paused())
		return 2;
	const bool on = obs_frontend_recording_active() || ClipBufferActive() || obs_frontend_streaming_active() || obs_frontend_virtualcam_active();
	return on ? 1 : 0;
}

void ClearTaskbarOverlay()
{
	QWidget *window = (QWidget *)obs_frontend_get_main_window();
	if (!window)
		return;
	ITaskbarList3 *taskbar = nullptr;
	if (FAILED(CoCreateInstance(__uuidof(TaskbarList), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&taskbar))))
		return;
	if (SUCCEEDED(taskbar->HrInit()))
		taskbar->SetOverlayIcon((HWND)window->winId(), nullptr, nullptr);
	taskbar->Release();
}

// kept only under the state it looks like, since obs can still be about to set the icon of the state it just reached
void RememberObsTrayIcon(const QIcon &icon, int state)
{
	if (icon.isNull())
		return;
	const bool dotted = RedDotPixels(icon) >= kRedDotPixels;
	if ((state == 0 && dotted) || (state == 1 && !dotted))
		return;
	g_obsTrayIcon[state] = icon;
}

// the icon of a state of the tray: the one obs set for that state when it was last seen, else the lookup obs itself makes (the theme name, then its own image), else the plain logo with the dot drawn on
QIcon ObsTrayIconFor(int state)
{
	if (!g_obsTrayIcon[state].isNull())
		return g_obsTrayIcon[state];
	static const char *const names[] = {"obs-tray", "obs-tray-active", "obs-tray-paused"};
	static const char *const files[] = {":/res/images/obs.png", ":/res/images/tray_active.png", ":/res/images/obs_paused.png"};
	const QIcon themed = QIcon::fromTheme(QLatin1String(names[state]), QIcon(QLatin1String(files[state])));
	if (!themed.isNull())
		return themed;
	return state == 0 ? g_appIconDefaultMain : ComposeRecordingDot(g_appIconDefaultMain);
}

// Win11 resolves a running windows taskbar-button icon through obs64.exe / its start-menu shortcut and can ignore WM_SETICON + the class icon. An explicit AppUserModelID makes it consult the icon we set. We only update that property; removing and re-adding the taskbar tab made every icon choice visibly flash.
void RetagTaskbarButton(HWND hwnd, bool force, const wchar_t *aumid)
{
	// PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5 -- inlined to skip the propsys.lib link.
	static const PROPERTYKEY kAumidKey = {
		{0x9F4C2855, 0x9F79, 0x4B39, {0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3}}, 5};

	IPropertyStore *store = nullptr;
	if (SUCCEEDED(SHGetPropertyStoreForWindow(hwnd, IID_PPV_ARGS(&store))) && store) {
		PROPVARIANT pv;
		PropVariantInit(&pv); // vt stays VT_EMPTY -> SetValue clears the property
		if (force && aumid) {
			size_t n = (wcslen(aumid) + 1) * sizeof(wchar_t);
			pv.pwszVal = (LPWSTR)CoTaskMemAlloc(n);
			if (pv.pwszVal) {
				memcpy(pv.pwszVal, aumid, n);
				pv.vt = VT_LPWSTR;
			}
		}
		store->SetValue(kAumidKey, pv);
		store->Commit();
		PropVariantClear(&pv);
		store->Release();
	}

}

// the real obs logo, read straight out of the icon resource of the exe. the icon obs hands to qt for its windows is a flat theme variant of it, which is what the title bar used to draw
QImage ImageFromHIcon(HICON icon, int size)
{
	// alpha comes from drawing the icon over black and over white: what differs between the two is exactly what the icon lets through
	auto render = [&](COLORREF background) {
		BITMAPINFO bi = {};
		bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
		bi.bmiHeader.biWidth = size;
		bi.bmiHeader.biHeight = -size;
		bi.bmiHeader.biPlanes = 1;
		bi.bmiHeader.biBitCount = 32;
		bi.bmiHeader.biCompression = BI_RGB;
		HDC screen = GetDC(nullptr);
		HDC dc = CreateCompatibleDC(screen);
		void *bits = nullptr;
		HBITMAP dib = CreateDIBSection(screen, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
		QImage out;
		if (dib && bits) {
			HGDIOBJ old = SelectObject(dc, dib);
			RECT r = {0, 0, size, size};
			HBRUSH brush = CreateSolidBrush(background);
			FillRect(dc, &r, brush);
			DeleteObject(brush);
			DrawIconEx(dc, 0, 0, icon, size, size, 0, nullptr, DI_NORMAL);
			GdiFlush();
			out = QImage((const uchar *)bits, size, size, size * 4, QImage::Format_RGB32).copy();
			SelectObject(dc, old);
		}
		if (dib)
			DeleteObject(dib);
		DeleteDC(dc);
		ReleaseDC(nullptr, screen);
		return out;
	};
	const QImage onBlack = render(RGB(0, 0, 0));
	const QImage onWhite = render(RGB(255, 255, 255));
	if (onBlack.isNull() || onWhite.isNull())
		return QImage();
	QImage result(size, size, QImage::Format_ARGB32);
	for (int y = 0; y < size; ++y) {
		for (int x = 0; x < size; ++x) {
			const QRgb b = onBlack.pixel(x, y);
			const QRgb w = onWhite.pixel(x, y);
			const int alpha = qBound(0, 255 - (qGreen(w) - qGreen(b)), 255);
			if (alpha == 0) {
				result.setPixel(x, y, 0);
				continue;
			}
			auto straight = [alpha](int c) { return qBound(0, c * 255 / alpha, 255); };
			result.setPixel(x, y, qRgba(straight(qRed(b)), straight(qGreen(b)), straight(qBlue(b)), alpha));
		}
	}
	return result;
}

QImage LoadExeLogo()
{
	wchar_t exe[MAX_PATH] = {};
	if (!GetModuleFileNameW(nullptr, exe, MAX_PATH))
		return QImage();
	HICON large = nullptr;
	// asked for at 256 so it can be scaled down smoothly at any dpi; the first icon of the exe is the logo
	if (FAILED(SHDefExtractIconW(exe, 0, 0, &large, nullptr, MAKELONG(256, 0))) || !large)
		return QImage();
	const QImage image = ImageFromHIcon(large, 256);
	DestroyIcon(large);
	return image;
}

} // namespace

HICON g_ownedIconSettings = nullptr;
HICON g_ownedIconSetup = nullptr;
int g_taggedSettings = -1;
int g_taggedSetup = -1;

// force a taskbar button on our own top-level windows. they normally get one for free, but RetagTaskbarButton rewrites their PKEY_AppUserModel_ID (Windows then rebuilds the button) and a stray owner would suppress it -- WS_EX_APPWINDOW makes the button unconditional. set while hidden, before show(), so it is honoured on the first paint.
void ForceTaskbarButton(QWidget *w)
{
	if (!w)
		return;
	HWND hwnd = (HWND)w->winId(); // realises the native window
	if (!hwnd)
		return;
	LONG_PTR ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
	SetWindowLongPtr(hwnd, GWL_EXSTYLE, (ex | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW);
}
QString g_rkIconPath;             // RKICON <path> -- the replaykit-branded .ico for our own windows when appIcon is default
bool g_recordingDotEnabled = true; // helper Appearance toggle (SETICONDOT)

// our own top-level windows (Clips, ReplayKit Settings) -- keep OBSs native icon by default, or use the selected custom icon. Every custom-icon window shares the main OBS taskbar identity so Windows groups them together.
void ApplyAuxWindowIcon(QWidget *w, HICON *owned, int *tagged, const wchar_t *aumid)
{
	if (!w)
		return;
	bool custom = !(g_appIconPath.isEmpty() || g_appIconPath == "-");
	// prefer OBSs loaded icon over the bundled fallback; the fallbacks old low-colour frames look distorted in a Windows caption at 16px.
	QIcon ic;
	if (custom)
		ic = QIcon(g_appIconPath);
	if (ic.isNull())
		ic = g_appIconDefaultMain;
	if (ic.isNull())
		ic = QGuiApplication::windowIcon(); // obs's own app icon -- always valid once obs is up
	if (ic.isNull() && !g_rkIconPath.isEmpty())
		ic = QIcon(g_rkIconPath);
	if (ic.isNull())
		return;

	w->setWindowIcon(ic);
	HWND hwnd = (HWND)w->winId();

	// only a user-selected icon needs its own taskbar identity. The OBS default keeps OBSs native title-bar and taskbar resolution path, which avoids scaling a 256px fallback image into the caption slot.
	bool forced = custom;
	if (forced) {
		HICON ni = HIconFromQIcon(ic, 256);
		if (ni) {
			SendMessage(hwnd, WM_SETICON, ICON_BIG, (LPARAM)ni);
			SendMessage(hwnd, WM_SETICON, ICON_SMALL, (LPARAM)ni);
			SendMessage(hwnd, WM_SETICON, ICON_SMALL2, (LPARAM)ni);
			HICON old = *owned;
			*owned = ni;
			if (old && old != ni)
				DestroyIcon(old);
		}
	} else if (*owned) {
		DestroyIcon(*owned);
		*owned = nullptr;
	}

    if (*tagged != (forced ? 1 : 0)) {
        RetagTaskbarButton(hwnd, forced, forced ? L"ReplayKit.OBSCustomIcon" : nullptr);
        *tagged = forced ? 1 : 0;
    }
}

// snapshot obss own window + class + tray icons before we touch anything. safe to call repeatedly.
void CaptureAppIconDefaults()
{
	if (g_appIconDefaultsCaptured)
		return;
	QWidget *mw = (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return; // main window not up yet -- try again later
	g_appIconDefaultMain = mw->windowIcon();
	HWND hwnd = (HWND)mw->winId();
	g_classOrigBig = (HICON)GetClassLongPtr(hwnd, GCLP_HICON);
	g_classOrigSmall = (HICON)GetClassLongPtr(hwnd, GCLP_HICONSM);
	if (QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray())
		RememberObsTrayIcon(tray->icon(), ObsTrayState());
	g_appIconDefaultsCaptured = true;
}

// the tray icon, and the only surface the recording dot is drawn on: obs shows what it is doing on the tray alone, so with a custom icon the window, the taskbar button and the title bar keep the plain icon too, and a recording that starts or stops only needs this
void RefreshTrayIcon()
{
	CaptureAppIconDefaults();
	ClearTaskbarOverlay();

	const bool custom = !(g_appIconPath.isEmpty() || g_appIconPath == "-");
	QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
	if (!tray)
		return;

	if (custom) {
		const QIcon base(g_appIconPath);
		if (base.isNull())
			return;
		// the dot only rides a user-chosen icon -- on "default" obs manages its own recording indicator.
		const bool recording = obs_frontend_recording_active() || obs_frontend_replay_buffer_active();
		const QIcon icon = recording && g_recordingDotEnabled ? ComposeRecordingDot(base) : base;
		tray->setIcon(icon);
		g_lastTrayIconKey = icon.cacheKey();
		g_trayIsOurs = true;
		return;
	}

	const int state = ObsTrayState();
	if (tray->icon().cacheKey() != g_lastTrayIconKey)
		RememberObsTrayIcon(tray->icon(), state);
	// OBS may replace the tray icon while hiding or restoring its window. Reapply the current state every time.
	g_trayIsOurs = !g_recordingDotEnabled && state == 1;
	const QIcon icon = ObsTrayIconFor(g_trayIsOurs ? 0 : state);
	tray->setIcon(icon);
	g_lastTrayIconKey = icon.cacheKey();
}

// the single place that pushes the current icon (custom-or-default) to every surface; the recording dot is not part of it, RefreshTrayIcon draws that on the tray alone.
void RefreshAppIcon()
{
	CaptureAppIconDefaults();

	bool custom = !(g_appIconPath.isEmpty() || g_appIconPath == "-");

	// nothing was ever swapped and nothing is requested -- leave obss own icon untouched. (a branded rk icon for our own windows still counts as something to do.)
	if (!custom && !g_appIconDefaultsCaptured && g_rkIconPath.isEmpty())
		return;

	QWidget *mw = (QWidget *)obs_frontend_get_main_window();

	QIcon baseMain = custom ? QIcon(g_appIconPath) : g_appIconDefaultMain;
	if (custom && baseMain.isNull()) {
		blog(LOG_WARNING, "[replaykit] SETICON: could not load '%s'", g_appIconPath.toUtf8().constData());
		return;
	}

	// the window, the taskbar button and the title bar wear the plain icon whatever obs is doing
	if (mw)
		mw->setWindowIcon(baseMain);
	RefreshTrayIcon();
	// Windows can use Qts process-wide icon for an existing taskbar button. Always restore it too; otherwise a prior preset remains cached there after the user returns to Default.
	if (!baseMain.isNull())
		QGuiApplication::setWindowIcon(baseMain);

	// win11 taskbar: needs a large WM_SETICON payload + the window class icon. push one 256px HICON to every slot, or restore obss own class icons (qts setWindowIcon above already put the default back on the WM_SETICON slots).
	if (mw) {
		HWND hwnd = (HWND)mw->winId();
		if (custom) {
			HICON ni = HIconFromQIcon(baseMain, 256);
			if (ni) {
				SetClassLongPtr(hwnd, GCLP_HICON, (LONG_PTR)ni);
				SetClassLongPtr(hwnd, GCLP_HICONSM, (LONG_PTR)ni);
				SendMessage(hwnd, WM_SETICON, ICON_BIG, (LPARAM)ni);
				SendMessage(hwnd, WM_SETICON, ICON_SMALL, (LPARAM)ni);
				SendMessage(hwnd, WM_SETICON, ICON_SMALL2, (LPARAM)ni);
				HICON old = g_ownedIcon;
				g_ownedIcon = ni;
				if (old && old != ni)
					DestroyIcon(old);
			}
		} else {
			SetClassLongPtr(hwnd, GCLP_HICON, (LONG_PTR)g_classOrigBig);
			SetClassLongPtr(hwnd, GCLP_HICONSM, (LONG_PTR)g_classOrigSmall);
			if (g_ownedIcon) {
				DestroyIcon(g_ownedIcon);
				g_ownedIcon = nullptr;
			}
		}

		// only update the per-window identity when its mode changes. RetagTaskbarButton deliberately avoids a DeleteTab/AddTab cycle, so this does not flash the button.
		if (g_taskbarTaggedCustom != (custom ? 1 : 0)) {
			RetagTaskbarButton(hwnd, custom, L"ReplayKit.OBSCustomIcon");
			g_taskbarTaggedCustom = custom ? 1 : 0;
		}
	}

	// our own windows follow OBSs native default or the selected custom icon. Custom-icon windows share the main OBS custom AUMID so they form one taskbar group instead of separate buttons.
	ApplyAuxWindowIcon(g_clipsWindow, &g_ownedIconClips, &g_taggedClips, L"ReplayKit.ClipsWindow");
	ApplyAuxWindowIcon(g_settingsWindow, &g_ownedIconSettings, &g_taggedSettings, L"ReplayKit.SettingsWindow");
	ApplyAuxWindowIcon(g_setupWindow, &g_ownedIconSetup, &g_taggedSetup, L"ReplayKit.SetupWindow");

	const bool recording = obs_frontend_recording_active() || obs_frontend_replay_buffer_active();
	blog(LOG_INFO, "[replaykit] icon refresh: custom=%d recording=%d", custom ? 1 : 0, recording ? 1 : 0);
}

// what the tray icon is right now and what obs has put on it, for the command channel
QJsonObject TrayIconReport()
{
	QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
	QJsonObject out{{"ok", true}, {"hasTray", tray != nullptr}, {"state", ObsTrayState()}, {"ours", g_trayIsOurs}};
	if (tray) {
		const int dots = RedDotPixels(tray->icon());
		out["visible"] = tray->isVisible();
		out["redPixels"] = dots;
		out["redDot"] = dots >= kRedDotPixels;
	}
	QJsonObject kept;
	for (int i = 0; i < 3; ++i)
		kept[QString::number(i)] = g_obsTrayIcon[i].isNull() ? -1 : RedDotPixels(g_obsTrayIcon[i]);
	out["keptRedPixels"] = kept;
	out["themeHas"] = QJsonObject{{"obs-tray", !QIcon::fromTheme(QStringLiteral("obs-tray")).isNull()}, {"obs-tray-active", !QIcon::fromTheme(QStringLiteral("obs-tray-active")).isNull()}};
	out["dotEnabled"] = g_recordingDotEnabled;
	// a fingerprint and the pure red pixels of what each surface wears, so a test can tell whether the recording dot reached it: only the tray may change when a recording starts
	const auto surface = [](const QImage &image) {
		QJsonObject s;
		if (image.isNull())
			return s;
		const QImage argb = image.convertToFormat(QImage::Format_ARGB32);
		s["hash"] = QString::fromLatin1(QCryptographicHash::hash(QByteArray((const char *)argb.constBits(), (int)argb.sizeInBytes()), QCryptographicHash::Md5).toHex().left(12));
		s["red"] = RedDotPixels(QIcon(QPixmap::fromImage(argb)));
		return s;
	};
	QJsonObject surfaces;
	if (QMainWindow *mw = ObsMainWindowQt()) {
		surfaces["window"] = surface(mw->windowIcon().pixmap(32, 32).toImage());
		const HWND hwnd = (HWND)mw->winId();
		HICON big = (HICON)SendMessage(hwnd, WM_GETICON, ICON_BIG, 0);
		if (!big)
			big = (HICON)GetClassLongPtr(hwnd, GCLP_HICON);
		if (big)
			surfaces["taskbar"] = surface(ImageFromHIcon(big, 32));
	}
	surfaces["application"] = surface(QGuiApplication::windowIcon().pixmap(32, 32).toImage());
	if (tray)
		surfaces["tray"] = surface(tray->icon().pixmap(32, 32).toImage());
	surfaces["titleLogo"] = surface(ShellLogoPixmap(26, 1.0).toImage());
	out["surfaces"] = surfaces;
	// whether the plain tray icon obs showed is the image the lookup above falls back on, which is what puts the plain icon back when the dot is off
	const QIcon obsPng(QStringLiteral(":/res/images/obs.png"));
	out["obsPngFound"] = !obsPng.isNull();
	if (!g_obsTrayIcon[0].isNull() && !obsPng.isNull())
		out["plainTrayIsObsPng"] = g_obsTrayIcon[0].pixmap(32, 32).toImage().convertToFormat(QImage::Format_ARGB32) == obsPng.pixmap(32, 32).toImage().convertToFormat(QImage::Format_ARGB32);
	return out;
}

// the icon the title bar wears: the one picked in the appearance tab when there is one (so it matches the taskbar), otherwise the icon of the obs exe itself
QPixmap ShellLogoPixmap(int logicalPx, qreal dpr)
{
	const int px = qMax(1, qRound(logicalPx * dpr));
	const bool custom = !(g_appIconPath.isEmpty() || g_appIconPath == "-");
	QImage img;
	if (custom) {
		QMainWindow *mw = ObsMainWindowQt();
		const QIcon icon = mw ? mw->windowIcon() : QIcon();
		if (!icon.isNull())
			img = icon.pixmap(px, px).toImage();
	}
	if (img.isNull()) {
		static const QImage exeLogo = LoadExeLogo();
		static QImage scaled;
		static int scaledPx = 0;
		if (exeLogo.isNull())
			return QPixmap();
		if (scaledPx != px) {
			scaled = exeLogo.scaled(px, px, Qt::KeepAspectRatio, Qt::SmoothTransformation);
			scaledPx = px;
		}
		img = scaled;
	}
	QPixmap pm = QPixmap::fromImage(img);
	pm.setDevicePixelRatio(dpr);
	return pm;
}

void ApplyAppIcon(const QString &path)
{
	g_appIconPath = path;
	CaptureAppIconDefaults();
	RefreshAppIcon();
}

} // namespace rk
