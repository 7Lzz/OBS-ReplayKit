// stage cover shown while the shell browser loads
#include "prelude.hpp"
#include "shell/shell_curtain.hpp"

#include "core/theme.hpp"

namespace rk {

ShellCurtain::ShellCurtain(QWidget *parent)
	: QWidget(parent)
{
	setAttribute(Qt::WA_NativeWindow, true);
	m_spin.setInterval(33);
	QObject::connect(&m_spin, &QTimer::timeout, this, [this]() { update(); });
}

void ShellCurtain::SetMessage(const QString &title, const QString &hint)
{
	m_title = title;
	m_hint = hint;
	if (m_title.isEmpty() && isVisible()) {
		m_clock.start();
		m_spin.start();
	} else if (!m_title.isEmpty()) {
		m_spin.stop();
	}
	update();
}

void ShellCurtain::showEvent(QShowEvent *event)
{
	QWidget::showEvent(event);
	m_clock.start();
	if (m_title.isEmpty())
		m_spin.start();
}

void ShellCurtain::hideEvent(QHideEvent *event)
{
	QWidget::hideEvent(event);
	m_spin.stop();
}

void ShellCurtain::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.fillRect(rect(), g_chromeBg);
	if (!m_title.isEmpty()) {
		p.setRenderHint(QPainter::TextAntialiasing, true);
		QFont head("Segoe UI");
		head.setPixelSize(16);
		head.setWeight(QFont::DemiBold);
		QFont note("Segoe UI");
		note.setPixelSize(12);
		p.setFont(head);
		p.setPen(ChromeInk(0.86));
		p.drawText(QRectF(0, height() / 2.0 - 30, width(), 28), Qt::AlignHCenter | Qt::AlignBottom, m_title);
		p.setFont(note);
		p.setPen(g_menuMuted);
		p.drawText(QRectF(0, height() / 2.0 + 2, width(), 22), Qt::AlignHCenter | Qt::AlignTop, m_hint);
		return;
	}
	// the spinner only appears after a moment, so a fast load never shows one
	if (!m_clock.isValid() || m_clock.elapsed() < 400)
		return;
	p.setRenderHint(QPainter::Antialiasing, true);
	QPen pen(ChromeInk(0.55), 2.5);
	pen.setCapStyle(Qt::RoundCap);
	p.setPen(pen);
	const QRectF ring(width() / 2.0 - 14, height() / 2.0 - 14, 28, 28);
	const int start = int(-(m_clock.elapsed() % 900) * 360 * 16 / 900);
	p.drawArc(ring, start, 100 * 16);
}

} // namespace rk
