// folds the obs settings dialog into the main window
#pragma once

#include <QEvent>
#include <QObject>

namespace rk {

// watches the first polish of every widget, so it bails on the event type first and only ever looks at a hidden top-level whose class name is the obs settings dialog
class ObsSettingsEmbedFilter : public QObject {
protected:
	bool eventFilter(QObject *watched, QEvent *event) override;
};

} // namespace rk
