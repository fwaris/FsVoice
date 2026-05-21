namespace Speak2Docs.Views

open Microsoft.Maui.ApplicationModel
open Microsoft.Maui.Graphics

module Theme =
    let private resolve appTheme =
        match appTheme with
        | AppTheme.Unspecified ->
            match AppInfo.Current.RequestedTheme with
            | AppTheme.Unspecified -> AppTheme.Light
            | requestedTheme -> requestedTheme
        | requestedTheme -> requestedTheme

    let private isDark appTheme =
        match resolve appTheme with
        | AppTheme.Dark -> true
        | AppTheme.Light
        | _ -> false

    let pageBackgroundColor appTheme =
        if isDark appTheme then
            Color.FromArgb("#111111")
        else
            Colors.White

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
