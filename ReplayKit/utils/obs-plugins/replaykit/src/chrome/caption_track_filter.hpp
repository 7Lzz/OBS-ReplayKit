// keeps the caption overlays glued to their window during a move loop
#pragma once

#include <QAbstractNativeEventFilter>
#include <QByteArray>
#include <QtGlobal>

namespace rk {

// window drags run inside a modal move loop that never gets back to qts move/resize events until it ends, which is why the button and panel lagged behind the window. WM_WINDOWPOSCHANGED is delivered throughout that loop, so tracking it natively is what makes them look attached.
class NotifyTrackFilter : public QAbstractNativeEventFilter {
public:
	bool nativeEventFilter(const QByteArray &, void *message, qintptr *) override;
};

} // namespace rk
