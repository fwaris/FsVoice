namespace Speak2Docs.Views

open Microsoft.Maui.ApplicationModel
open Microsoft.Maui.Graphics

module Theme =
    let private isDark appTheme =
        match appTheme with
        | AppTheme.Dark -> true
        | AppTheme.Light
        | AppTheme.Unspecified -> false
        | _ -> false

    let textColor appTheme =
        if isDark appTheme then Colors.White else Colors.Black

    let secondaryTextColor appTheme =
        if isDark appTheme then
            Color.FromArgb("#D6D6D6")
        else
            Color.FromArgb("#4B5563")

    let mutedTextColor appTheme =
        if isDark appTheme then
            Color.FromArgb("#B8B8B8")
        else
            Color.FromArgb("#6B7280")

    let borderColor appTheme =
        if isDark appTheme then
            Color.FromArgb("#5A5A5A")
        else
            Color.FromArgb("#D1D5DB")

    let readyColor appTheme =
        if isDark appTheme then
            Color.FromArgb("#7EE787")
        else
            Colors.SeaGreen

    let processingColor appTheme =
        if isDark appTheme then
            Color.FromArgb("#FFB454")
        else
            Colors.DarkOrange

    let failedColor appTheme =
        if isDark appTheme then
            Color.FromArgb("#FF8A8A")
        else
            Colors.Firebrick
