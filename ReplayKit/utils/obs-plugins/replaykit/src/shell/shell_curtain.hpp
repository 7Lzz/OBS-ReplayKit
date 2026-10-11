// stage cover shown while the shell browser loads
#pragma once

#include <QElapsedTimer>
#include <QHideEvent>
#include <QPaintEvent>
#include <QShowEvent>
#include <QString>
#include <QTimer>
#include <QWidget>

namespace rk {

// covers the stage while the browser loads its first page: the window colour with a quiet spinner, so the wait reads as loading and not as an empty window. a native window because the preview it hides is one
class ShellCurtain : public QWidget {
public:
	explicit ShellCurtain(QWidget *parent);

	// a plain message on the ground in place of the spinner, for a stage that has nothing to show on purpose (the preview turned off); empty strings bring the spinner back
	void SetMessage(const QString &title, const QString &hint);
	bool HasMessage() const { return !m_title.isEmpty(); }

protected:
	void showEvent(QShowEvent *event) override;
	void hideEvent(QHideEvent *event) override;
	void paintEvent(QPaintEvent *) override;

private:
	QString m_title;
	QString m_hint;
	QTimer m_spin;
	QElapsedTimer m_clock;
};

} // namespace rk
