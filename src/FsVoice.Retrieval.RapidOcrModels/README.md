# FsVoice.Retrieval.RapidOcrModels.PpOcrV4.Mobile

Embedded RapidOCR PP-OCRv4 mobile ONNX model assets for FsVoice optical PDF parsing.

The package contains the model files expected by `RapidOCR.Net`:

- `ch_PP-OCRv4_det_mobile.onnx`
- `ch_ppocr_mobile_v2.0_cls_mobile.onnx`
- `ch_PP-OCRv4_rec_mobile.onnx`
- `ppocr_keys_v1.txt`

Applications can reference this package to enable offline optical parsing for
PDF pages where native text extraction fails or returns too little text.

## Runtime Extraction

The model files are embedded as assembly resources. Call:

```fsharp
FsVoice.Retrieval.RapidOcrModels.RapidOcrPpOcrV4Mobile.EnsureExtracted(storageRoot)
```

The assets are extracted and verified under:

```text
FsVoice/FsColbert/Models/rapidocr/pp-ocrv4-mobile/
```

`FsVoice.Retrieval` also probes this companion package dynamically when optical
parsing is enabled.

## Licensing

The package license expression is `MIT AND Apache-2.0`.

See `THIRD-PARTY-NOTICES.md` and `MODEL-MANIFEST.json` for attribution and
checksums.
