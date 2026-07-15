namespace Speak2Docs

open Microsoft.Maui.ApplicationModel
#if ANDROID
open Android.Content
open Android.Media
open Android.OS
#endif

module Audio =
#if ANDROID
    let private isModernCommunicationRouting () =
        Build.VERSION.SdkInt >= BuildVersionCodes.S

    let private communicationDevices (audioManager: AudioManager) =
        audioManager.AvailableCommunicationDevices
        |> Seq.cast<AudioDeviceInfo>
        |> Seq.toList

    let private communicationDevicePriority (device: AudioDeviceInfo) =
        match device.Type with
        | AudioDeviceType.BluetoothSco -> Some 0
        | AudioDeviceType.BleHeadset -> Some 1
        | AudioDeviceType.WiredHeadset -> Some 2
        | AudioDeviceType.UsbHeadset -> Some 3
        | AudioDeviceType.WiredHeadphones -> Some 4
        | AudioDeviceType.BluetoothA2dp -> Some 5
        | AudioDeviceType.BuiltinEarpiece -> Some 6
        | _ -> None

    let private headsetCommunicationDevicePriority (device: AudioDeviceInfo) =
        match device.Type with
        | AudioDeviceType.BluetoothSco -> Some 0
        | AudioDeviceType.BleHeadset -> Some 1
        | AudioDeviceType.WiredHeadset -> Some 2
        | AudioDeviceType.UsbHeadset -> Some 3
        | AudioDeviceType.WiredHeadphones -> Some 4
        | AudioDeviceType.HearingAid -> Some 5
        | AudioDeviceType.BluetoothA2dp -> Some 6
        | _ -> None

    let private preferredCommunicationDevice (devices: AudioDeviceInfo list) =
        devices
        |> List.choose (fun device ->
            communicationDevicePriority device
            |> Option.map (fun priority -> priority, device))
        |> List.sortBy fst
        |> List.tryHead
        |> Option.map snd

    let private preferredHeadsetCommunicationDevice (devices: AudioDeviceInfo list) =
        devices
        |> List.choose (fun device ->
            headsetCommunicationDevicePriority device
            |> Option.map (fun priority -> priority, device))
        |> List.sortBy fst
        |> List.tryHead
        |> Option.map snd

    let private speakerCommunicationDevice (devices: AudioDeviceInfo list) =
        devices
        |> List.tryFind (fun device -> device.Type = AudioDeviceType.BuiltinSpeaker)

    let private trySetCommunicationDevice (audioManager: AudioManager) (device: AudioDeviceInfo) =
        try
            audioManager.SetCommunicationDevice(device)
        with ex ->
            System.Diagnostics.Debug.WriteLine($"Unable to set Android communication device: {ex.Message}")
            false

    let private applyAndroidSpeakerRoute (audioManager: AudioManager) =
        if isModernCommunicationRouting () then
            let devices = communicationDevices audioManager

            match preferredHeadsetCommunicationDevice devices with
            | Some device ->
                audioManager.SpeakerphoneOn <- false
                trySetCommunicationDevice audioManager device |> ignore
            | None ->
                audioManager.SpeakerphoneOn <- true

                devices
                |> speakerCommunicationDevice
                |> Option.iter (fun device -> trySetCommunicationDevice audioManager device |> ignore)
        else
            audioManager.SpeakerphoneOn <- true

    let private applyAndroidHeadsetRoute (audioManager: AudioManager) =
        audioManager.SpeakerphoneOn <- false

        if isModernCommunicationRouting () then
            match communicationDevices audioManager |> preferredCommunicationDevice with
            | Some device -> trySetCommunicationDevice audioManager device |> ignore
            | None -> audioManager.ClearCommunicationDevice()
        else
            audioManager.BluetoothScoOn <- true
            audioManager.StartBluetoothSco()
#endif

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

                if defaultToSpeaker then
                    applyAndroidSpeakerRoute audioManager
                else
                    applyAndroidHeadsetRoute audioManager
        with ex ->
            System.Diagnostics.Debug.WriteLine($"Unable to apply Android speaker route: {ex.Message}")
#else
        ignore defaultToSpeaker
#endif
