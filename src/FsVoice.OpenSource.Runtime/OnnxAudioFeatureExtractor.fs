namespace FsVoice.OpenSource

open System
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors

[<RequireQualifiedAccess>]
module internal OnnxAudioFeatureExtractor =
    // Opset-17 graph: STFT -> magnitude -> mel projection -> floor -> log.
    // Window and mel weights remain inputs so model-specific values stay in GemmaProcessor.
    [<Literal>]
    let private ModelBase64 =
        "CAkSIEZzVm9pY2UgT05OWCBhdWRpbyBwcmVwcm9jZXNzaW5nOs4ECkcKBnNpZ25hbAoKZnJhbWVfc3RlcAoGd2luZG93CgxmcmFtZV9sZW5ndGgSBHN0ZnQiBFNURlQqDwoIb25lc2lkZWQYAaABAgoaCgRzdGZ0CgRzdGZ0EgdzcXVhcmVkIgNNdWwKOgoHc3F1YXJlZAoMY29tcGxleF9heGlzEgVwb3dlciIJUmVkdWNlU3VtKg8KCGtlZXBkaW1zGACgAQIKGAoFcG93ZXISCW1hZ25pdHVkZSIEU3FydAouCgltYWduaXR1ZGUKC21lbF9maWx0ZXJzEgxtZWxfc3BlY3RydW0iBk1hdE11bAonCgxtZWxfc3BlY3RydW0KCW1lbF9mbG9vchIHZmxvb3JlZCIDQWRkChgKB2Zsb29yZWQSCGZlYXR1cmVzIgNMb2cSGUZzVm9pY2VHZW1tYUF1ZGlvRmVhdHVyZXMqHAgBEAdCDGNvbXBsZXhfYXhpc0oI//////////9aKQoGc2lnbmFsEh8KHQgBEhkKAggBCg8SDXNpZ25hbF9sZW5ndGgKAggBWhQKCmZyYW1lX3N0ZXASBgoECAcSAFoeCgZ3aW5kb3cSFAoSCAESDgoMEgpmZnRfbGVuZ3RoWhYKDGZyYW1lX2xlbmd0aBIGCgQIBxIAWikKC21lbF9maWx0ZXJzEhoKGAgBEhQKBhIEYmlucwoKEghmZWF0dXJlc1oTCgltZWxfZmxvb3ISBgoECAESAGIsCghmZWF0dXJlcxIgCh4IARIaCgIIAQoIEgZmcmFtZXMKChIIZmVhdHVyZXNCBAoAEBE="

    let private session =
        lazy
            let bytes = Convert.FromBase64String ModelBase64
            new InferenceSession(bytes)

    let extract
        (signal: float32 array)
        (hopLength: int)
        (fftLength: int)
        (window: float32 array)
        (melFilters: float32 array)
        (frequencyBins: int)
        (featureSize: int)
        (melFloor: float32)
        =
        let signalTensor = DenseTensor<float32>(signal, [| 1; signal.Length; 1 |])

        let frameStepTensor =
            DenseTensor<int64>(Memory<int64>([| int64 hopLength |]), ReadOnlySpan<int>.Empty)

        let frameLengthTensor =
            DenseTensor<int64>(Memory<int64>([| int64 fftLength |]), ReadOnlySpan<int>.Empty)

        let windowTensor = DenseTensor<float32>(window, [| window.Length |])
        let melTensor = DenseTensor<float32>(melFilters, [| frequencyBins; featureSize |])

        let floorTensor =
            DenseTensor<float32>(Memory<float32>([| melFloor |]), ReadOnlySpan<int>.Empty)

        let inputs =
            [ NamedOnnxValue.CreateFromTensor("signal", signalTensor)
              NamedOnnxValue.CreateFromTensor("frame_step", frameStepTensor)
              NamedOnnxValue.CreateFromTensor("window", windowTensor)
              NamedOnnxValue.CreateFromTensor("frame_length", frameLengthTensor)
              NamedOnnxValue.CreateFromTensor("mel_filters", melTensor)
              NamedOnnxValue.CreateFromTensor("mel_floor", floorTensor) ]

        use results = session.Value.Run inputs
        results |> Seq.head |> _.AsTensor<float32>() |> Seq.toArray
