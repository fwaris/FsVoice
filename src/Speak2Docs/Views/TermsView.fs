namespace Speak2Docs.Views

open Fabulous.Maui
open Speak2Docs
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Graphics
open type Fabulous.Maui.View

module TermsView =
    let private accentColor = Colors.DarkMagenta
    let private controlWidth = 280.

    let private message appTheme text =
        Label(text)
            .font(size = 15.)
            .textColor(Theme.textColor appTheme)
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

    let private secondaryButton appTheme text (msg: Msg) =
        Button(text, msg)
            .font(size = 16., attributes = FontAttributes.Bold)
            .background(Colors.Transparent)
            .textColor(Theme.textColor appTheme)
            .cornerRadius(8)
            .width(controlWidth)
            .height (52.)

    let private page appTheme =
        ContentPage(
            Grid([ Dimension.Star ], [ Dimension.Star ]) {
                (VStack(spacing = 14.) {
                    message appTheme "Review the Terms of Use and Privacy Policy before continuing."
                    linkButton "Terms of Use" TermsOfUse
                    linkButton "Privacy Policy" PrivacyPolicy
                    actionButton "Accept Terms & Privacy" TermsAccepted
                    secondaryButton appTheme "Do Not Accept" TermsDeclined
                })
                    .padding(24.)
                    .centerHorizontal()
                    .centerVertical ()
            }
        )
            .background(Theme.pageBackgroundColor appTheme)
            .title ("Terms")

    let contentPage (model: Model) = page model.appTheme
