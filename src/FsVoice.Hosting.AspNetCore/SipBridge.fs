namespace FsVoice.Hosting.AspNetCore

open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Options

[<RequireQualifiedAccess>]
module SipBridge =
    let private postConfigureOpenAiOptions (options: OpenAiRealtimeOptions) =
        let envApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")

        if
            String.IsNullOrWhiteSpace options.ApiKey
            && not (String.IsNullOrWhiteSpace envApiKey)
        then
            options.ApiKey <- envApiKey

    let addSipListener<'ToHost, 'FromHost>
        (createSessionOptions: SipVoiceSessionFactory<'ToHost, 'FromHost>)
        (hostAdapter: SipRealtimeHostAdapter<'ToHost, 'FromHost>)
        (services: IServiceCollection)
        =
        services.AddOptions<SipListenerOptions>() |> ignore
        services.AddOptions<OpenAiRealtimeOptions>() |> ignore

        services.PostConfigure<OpenAiRealtimeOptions>(Action<OpenAiRealtimeOptions>(postConfigureOpenAiOptions))
        |> ignore

        services.AddHttpClient("OpenAI") |> ignore

        services.TryAddSingleton<IOpenAiRealtimeRestClient, OpenAiRealtimeRestClient>()
        services.TryAddSingleton<IOpenAiWebRtcConnectionGate, OpenAiWebRtcConnectionGate>()
        services.TryAddSingleton<IOpenAiRealtimeWebRtcSessionFactory, OpenAiRealtimeWebRtcSessionFactory>()

        services.AddSingleton<SipListenerRegistration<'ToHost, 'FromHost>>(
            { createSessionOptions = createSessionOptions
              hostAdapter = hostAdapter }
        )
        |> ignore

        services.AddSingleton<SipCallBridge<'ToHost, 'FromHost>>() |> ignore
        services.AddHostedService<SipHostedService<'ToHost, 'FromHost>>() |> ignore
        services

    let addSipListenerWithConfiguration<'ToHost, 'FromHost>
        (configuration: IConfiguration)
        (createSessionOptions: SipVoiceSessionFactory<'ToHost, 'FromHost>)
        (hostAdapter: SipRealtimeHostAdapter<'ToHost, 'FromHost>)
        (services: IServiceCollection)
        =
        services.Configure<SipListenerOptions>(configuration.GetSection("Sip"))
        |> ignore

        services.Configure<OpenAiRealtimeOptions>(configuration.GetSection("OpenAI"))
        |> ignore

        addSipListener createSessionOptions hostAdapter services
