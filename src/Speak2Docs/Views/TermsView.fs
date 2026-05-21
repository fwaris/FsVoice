namespace Speak2Docs.Views

open Fabulous.Maui
open Speak2Docs
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module TermsView =
    let private bgColor = Colors.White
    let private primaryColor = Colors.Black
    let private accentColor = Colors.DarkMagenta
    let private controlWidth = 280.

    let private message text =
        Label(text)
            .font(size = 15.)
            .textColor(primaryColor)
            .lineBreakMode(LineBreakMode.WordWrap)
            .horizontalTextAlignment(TextAlignment.Center)
            .width (320.)

    let private linkButton text (link: AppLink) =
        Button(text, OpenAppLink link)
            .font(size = 18., attributes = FontAttributes.Bold)
            .background(Colors.Transparent)
            .textColor(accentColor)
            .width(controlWidth)
            .height (52.)

    let private actionButton text (msg: Msg) =
        Button(text, msg)
            .font(size = 16., attributes = FontAttributes.Bold)
            .background(accentColor)
            .textColor(Colors.White)
            .lineBreakMode(LineBreakMode.WordWrap)
            .cornerRadius(8)
            .width(controlWidth)
            .height (52.)

    let private secondaryButton text (msg: Msg) =
        Button(text, msg)
            .font(size = 16., attributes = FontAttributes.Bold)
            .background(Colors.Transparent)
            .textColor(primaryColor)
            .cornerRadius(8)
            .width(controlWidth)
            .height (52.)

    let private page () =
        ContentPage(
            Grid([ Dimension.Star ], [ Dimension.Star ]) {
                (VStack(spacing = 14.) {
                    message "Review the Terms of Use and Privacy Policy before continuing."
                    linkButton "Terms of Use" TermsOfUse
                    linkButton "Privacy Policy" PrivacyPolicy
                    actionButton "Accept Terms & Privacy" TermsAccepted
                    secondaryButton "Do Not Accept" TermsDeclined
                })
                    .padding(24.)
                    .centerHorizontal()
                    .centerVertical ()
            }
        )
            .background(bgColor)
            .title ("Terms")

    let contentPage (_model: Model) = page ()
