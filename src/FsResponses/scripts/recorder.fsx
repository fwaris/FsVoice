#r "nuget: PuppeteerSharp"

open System
open System.Threading.Tasks
open PuppeteerSharp

let recordUserActions () = task {
    // Launch browser
    let! browser = Puppeteer.LaunchAsync(LaunchOptions(Headless = false))
    let! page = browser.NewPageAsync()

    // Attach to console events to capture user interactions
    page.Console.Add(fun args ->
        printfn "[Browser Log] %s" args.Message.Text
    )

    // Navigate to the desired page
    let! _ = page.GoToAsync("https://google.com")

    // Inject JavaScript to track user actions
    let script = """
        () => {
            document.addEventListener('click', (e) => {
                console.log(JSON.stringify({
                    event: 'click',
                    tag: e.target.tagName,
                    id: e.target.id,
                    className: e.target.className
                }));
            });

            document.addEventListener('input', (e) => {
                console.log(JSON.stringify({
                    event: 'input',
                    tag: e.target.tagName,
                    id: e.target.id,
                    value: e.target.value
                }));
            });

            document.addEventListener('keydown', (e) => {
                console.log(JSON.stringify({
                    event: 'keydown',
                    key: e.key
                }));
            });
        }
    """
    let! _ = page.EvaluateFunctionAsync(script)

    printfn "Recording user actions... Press Enter to stop."
    Console.ReadLine() |> ignore

    do! browser.CloseAsync()
}

recordUserActions().GetAwaiter().GetResult()

