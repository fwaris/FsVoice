namespace FsVoice.Ctx

type GenericQaPlugIn() =
    interface IQaPlugIn with
        member _.ContractVersion = PlugInDefinition.currentContractVersion
        member _.Definition = PlugInDefinition.generic
        member _.GetToolProviders() = []
        member _.GetContextProviders _ = []
