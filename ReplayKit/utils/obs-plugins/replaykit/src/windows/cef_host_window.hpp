// top-level window that hosts a cef page and is hidden, never destroyed
#pragma once

#include <QCloseEvent>
#include <QHideEvent>
#include <QPointer>
#include <QShowEvent>
#include <QWidget>

#include "browser-panel.hpp"

namespace rk {

// settings + setup host a cef browser the same way clips does, so they share its close rule -- hide, never destroy. destroying a cef-hosting qwidget mid-session trips the obs-browser#353 teardown race: the freed child browser keeps being asked to realise a native window every frame (CreateWindowEx "the parameter is incorrect", on repeat) and pegs the ui thread until obs is force-killed. the real delete is CloseCefWidgetsBeforeShutdown at module unload, the one point cef teardown is safe.
class CefHostWindow : public QWidget {
public:
	// the page this window hosts, set as soon as its browser exists, so show and hide can be told to it
	QPointer<QCefWidget> browser;

protected:
	void closeEvent(QCloseEvent *event) override;
	// a hidden window keeps its page alive, so the page is told when it stops and starts being on screen (minimize and restore count too) and can drop things like the microphone while nobody is looking
	void showEvent(QShowEvent *event) override;
	void hideEvent(QHideEvent *event) override;
};

} // namespace rk
