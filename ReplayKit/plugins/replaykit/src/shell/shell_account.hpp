// the streamable account circle of the title bar
#pragma once

#include <QPixmap>
#include <QPointer>
#include <QString>

#include "shell/shell_title_button.hpp"

namespace rk {

class AnchoredPanel;

// the account circle next to the bell. anonymous while nobody is signed in to streamable, a coloured circle with the first letter of the account while somebody is, and the picture picked in the account menu over either. a press opens the account menu under it, where signing in or out and picking the picture happen
class ShellAccountButton : public ShellTitleButton {
public:
	static constexpr int kWidth = 40;

	// what the helper said about the streamable account
	struct Account {
		bool signedIn = false;
		QString name;
		// the version of the picture the helper keeps, empty when there is none
		QString picture;
	};

	ShellAccountButton(QWidget *owner, QWidget *parent);
	~ShellAccountButton() override;

	void SetAccount(const Account &account);
	const Account &GetAccount() const { return m_account; }
	// opens the menu under the button, or closes it again
	void TogglePanel();
	AnchoredPanel *Panel() const { return m_panel; }
	// makes the panel of the menu when it is not there yet
	AnchoredPanel *EnsurePanel();

protected:
	void PaintGlyph(QPainter &p, const QRectF &pill, qreal hover) override;

private:
	QPointer<QWidget> m_owner;
	Account m_account;
	QPixmap m_picture;
	// the picture brought down to the pixels of the circle once, since a plain smooth draw from 256px leaves it grainy
	QPixmap m_pictureScaled;
	AnchoredPanel *m_panel = nullptr;
};

// asks the helper who is signed in and hands the answer to the title bar; one request at a time
void ShellAccountPoll();

} // namespace rk
