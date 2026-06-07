namespace Speak2Docs

open Microsoft.Maui.ApplicationModel
#if ANDROID
open Android.Content
open Android.Media
#endif

module Audio =
    let haveRecordPermission () =
        task {
            let! permission =
                MainThread.InvokeOnMainThreadAsync<PermissionStatus>(fun () ->
                    Permissions.RequestAsync<Permissions.Microphone>())

            return permission = PermissionStatus.Granted
        }
        |> Async.AwaitTask

    let applyDefaultToSpeaker defaultToSpeaker =
#if ANDROID
        try
            let context = Platform.AppContext

            if not (isNull context) then
                let audioManager = context.GetSystemService(Context.AudioService) :?> AudioManager
                audioManager.Mode <- Mode.InCommunication
                audioManager.SpeakerphoneOn <- defaultToSpeaker
        with ex ->
            System.Diagnostics.Debug.WriteLine($"Unable to apply Android speaker route: {ex.Message}")
#else
        ignore defaultToSpeaker
#endif
