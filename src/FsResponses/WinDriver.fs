namespace FsOpWinDriver
open System
open System.IO
open System.Drawing
open System.Drawing.Imaging
open System.Diagnostics
open System.Runtime.InteropServices
open WindowsInput



module WDriver =
(*
    type EnumWindowsProc = delegate of IntPtr * IntPtr -> bool

    // Define necessary Win32 structures and functions
    [<StructLayout(LayoutKind.Sequential)>]
    type RECT =
        struct
            val mutable Left: int
            val mutable Top: int
            val mutable Right: int
            val mutable Bottom: int
        end

    [<DllImport("user32.dll")>]
    extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam)

    [<DllImport("user32.dll")>]
    extern bool IsWindowVisible(IntPtr hWnd)

    [<DllImport("user32.dll")>]
    extern uint32 GetWindowThreadProcessId(IntPtr hWnd, uint32& lpdwProcessId)

    [<DllImport("user32.dll")>]
    extern bool GetWindowRect(IntPtr hWnd, RECT& lpRect)

    [<DllImport("user32.dll")>]
    extern IntPtr GetDesktopWindow()

    [<DllImport("user32.dll")>]
    extern IntPtr GetDC(IntPtr hWnd)

    [<DllImport("gdi32.dll")>]
    extern IntPtr CreateCompatibleDC(IntPtr hdc)

    [<DllImport("gdi32.dll")>]
    extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight)

    [<DllImport("gdi32.dll")>]
    extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject)

    [<DllImport("gdi32.dll")>]
    extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                       IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop)

    [<DllImport("gdi32.dll")>]
    extern bool DeleteObject(IntPtr hObject)

    [<DllImport("gdi32.dll")>]
    extern bool DeleteDC(IntPtr hdc)

    [<DllImport("user32.dll")>]
    extern int ReleaseDC(IntPtr hWnd, IntPtr hDC)

    let SRCCOPY = 0x00CC0020u

    // Function to find the topmost visible window for a given process ID
    let findTopmostWindow (targetPid: int) : IntPtr option =
        let mutable result = IntPtr.Zero
        let callback = EnumWindowsProc(fun hWnd _ ->
            let mutable pid = 0u
            GetWindowThreadProcessId(hWnd, &pid) |> ignore
            if pid = uint32 targetPid && IsWindowVisible(hWnd) then
                result <- hWnd
                false // Stop enumeration
            else
                true  // Continue enumeration
        )
        EnumWindows(callback, IntPtr.Zero) |> ignore
        if result <> IntPtr.Zero then Some result else None

    // Define the MONITORINFO structure
    [<StructLayout(LayoutKind.Sequential)>]
    type MONITORINFO =
        struct
            val mutable cbSize: uint32
            val mutable rcMonitor: RECT
            val mutable rcWork: RECT
            val mutable dwFlags: uint32
        end

    // Import the MonitorFromWindow function
    [<DllImport("user32.dll")>]
    extern IntPtr MonitorFromWindow(IntPtr hwnd, uint32 dwFlags)

    // Import the GetMonitorInfo function
    [<DllImport("user32.dll", CharSet = CharSet.Auto)>]
    extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO& lpmi)

    // Constant for MonitorFromWindow
    let MONITOR_DEFAULTTONEAREST = 0x00000002u

    // Function to get the monitor size for a given window handle
    let getMonitorSizeForWindow (hwnd: IntPtr) =
        let hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
        if hMonitor = IntPtr.Zero then
            None
        else
            let mutable mi = MONITORINFO(cbSize = uint32 (Marshal.SizeOf(typeof<MONITORINFO>)))
            if GetMonitorInfo(hMonitor, &mi) then
                let width = mi.rcMonitor.Right - mi.rcMonitor.Left
                let height = mi.rcMonitor.Bottom - mi.rcMonitor.Top
                Some (width, height)
            else
                None

    // Function to capture a 1280x768 screenshot centered on the specified window
    let captureWindowScreenshot (hWnd: IntPtr) : Bitmap option =
        let mutable rect = RECT()
        if GetWindowRect(hWnd, &rect) then
            let windowWidth = rect.Right - rect.Left
            let windowHeight = rect.Bottom - rect.Top
            let windowCenterX = rect.Left + windowWidth / 2
            let windowTop = rect.Top

            // Desired capture size
            let captureWidth = 1280
            let captureHeight = 768

            // Calculate top-left corner of the capture region
            let captureLeft = windowCenterX - captureWidth / 2
            let captureTop = windowTop

            let sz = getMonitorSizeForWindow(hWnd)
            let screenWidth, screenHeight = sz |> Option.defaultValue (windowWidth,windowHeight)

            let adjustedLeft = Math.Max(0, Math.Min(captureLeft, screenWidth - captureWidth))
            let adjustedTop = Math.Max(0, Math.Min(captureTop, screenHeight - captureHeight))

            // Create bitmap and perform BitBlt
            let hdcScreen = GetDC(IntPtr.Zero)
            let hdcMemDC = CreateCompatibleDC(hdcScreen)
            let hBitmap = CreateCompatibleBitmap(hdcScreen, captureWidth, captureHeight)
            let hOld = SelectObject(hdcMemDC, hBitmap)
            BitBlt(hdcMemDC, 0, 0, captureWidth, captureHeight, hdcScreen, adjustedLeft, adjustedTop, SRCCOPY) |> ignore
            let bmp = Image.FromHbitmap(hBitmap)
            // Cleanup
            SelectObject(hdcMemDC, hOld) |> ignore
            DeleteObject(hBitmap) |> ignore
            DeleteDC(hdcMemDC) |> ignore
            ReleaseDC(IntPtr.Zero, hdcScreen) |> ignore
            Some (new Bitmap(bmp))
        else
            None

// Function to capture a 1280x768 screenshot centered over the target window
    let captureWindowScreenshotBlack (hWnd: IntPtr) : Bitmap option =
        let mutable rect = RECT()
        if GetWindowRect(hWnd, &rect) then
            let windowWidth = rect.Right - rect.Left
            let windowHeight = rect.Bottom - rect.Top
            let windowCenterX = rect.Left + windowWidth / 2
            let windowCenterY = rect.Top + windowHeight / 2

            // Desired capture size
            let captureWidth = 1280
            let captureHeight = 768

            // Calculate top-left corner of the capture region
            let captureLeft = windowCenterX - captureWidth / 2
            let captureTop = windowCenterY - captureHeight / 2

            // Create a bitmap with black background
            let bitmap = new Bitmap(captureWidth, captureHeight, PixelFormat.Format32bppArgb)
            use graphics = Graphics.FromImage(bitmap)
            graphics.Clear(Color.Black)

            // Get device contexts
            let hdcWindow = GetDC(hWnd)
            let hdcMemDC = CreateCompatibleDC(hdcWindow)
            let hBitmap = bitmap.GetHbitmap()
            let hOld = SelectObject(hdcMemDC, hBitmap)

            // Calculate the area to copy
            let srcX = Math.Max(0, captureLeft - rect.Left)
            let srcY = Math.Max(0, captureTop - rect.Top)
            let destX = Math.Max(0, rect.Left - captureLeft)
            let destY = Math.Max(0, rect.Top - captureTop)
            let copyWidth = Math.Min(windowWidth - srcX, captureWidth - destX)
            let copyHeight = Math.Min(windowHeight - srcY, captureHeight - destY)

            // Perform BitBlt
            BitBlt(hdcMemDC, destX, destY, copyWidth, copyHeight, hdcWindow, srcX, srcY, SRCCOPY) |> ignore

            // Cleanup
            SelectObject(hdcMemDC, hOld) |> ignore
            DeleteObject(hBitmap) |> ignore
            DeleteDC(hdcMemDC) |> ignore
            ReleaseDC(hWnd, hdcWindow) |> ignore

            Some bitmap
        else
            None

    [<DllImport("user32.dll")>]
    extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint32 nFlags)

    let captureWindow (hwnd: IntPtr) =
        let mutable rect = RECT()
        GetWindowRect(hwnd, &rect) |> ignore
        let width = rect.Right - rect.Left
        let height = rect.Bottom - rect.Top
        let bmp = new Bitmap(width, height, Imaging.PixelFormat.Format32bppArgb)
        let gfxBmp = Graphics.FromImage(bmp)
        let hdcBitmap = gfxBmp.GetHdc()
        if not (PrintWindow(hwnd, hdcBitmap, 0u)) then
            let error = Marshal.GetLastWin32Error()
            let exn = new System.ComponentModel.Win32Exception(error)
            printfn "ERROR: %d: %s" error exn.Message
        gfxBmp.ReleaseHdc(hdcBitmap)
        gfxBmp.Dispose()
        Some bmp

    /// Captures a bitmap of the specified window using PrintWindow
    let captureWindowBitmap (hWnd: IntPtr) : Bitmap option =
        let mutable rect = RECT()
        if GetWindowRect(hWnd, &rect) then
            let width = rect.Right - rect.Left
            let height = rect.Bottom - rect.Top

            // Create a bitmap to hold the screenshot
            use bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb)
            use graphics = Graphics.FromImage(bmp)
            let hdc = graphics.GetHdc()

            // Capture the window image into the bitmap
            let success = PrintWindow(hWnd, hdc, 0u)

            graphics.ReleaseHdc(hdc)

            if success then
                Some (bmp.Clone() :?> Bitmap)
            else
                None
        else
            None       


    // Example usage
    let captureProcessWindow (processName: string) =
        let processes = Process.GetProcessesByName(processName)
        if processes.Length > 0 then
            let hWndOption = findTopmostWindow processes.[0].Id
            match hWndOption with
            | Some hWnd ->
                match captureWindowScreenshot hWnd with
                | Some bmp ->
                    bmp.Save("screenshot.png", ImageFormat.Png)
                    printfn "Screenshot saved."
                | None ->
                    printfn "Failed to capture screenshot."
            | None ->
                printfn "No visible window found for process."
        else
            printfn "Process not found."

    let captureWindowBitBlt (hWnd: IntPtr) : Bitmap option =
        let mutable rect = Win32.RECT()
        if not (Win32.GetWindowRect(hWnd, &rect)) then
            None
        else
            let width = rect.Right - rect.Left
            let height = rect.Bottom - rect.Top
            let hWndDC = GetDC(hWnd)
            let hMemDC = CreateCompatibleDC(hWndDC)
            let hBitmap = CreateCompatibleBitmap(hWndDC, width, height)
            let hOld = Win32.SelectObject(hMemDC, hBitmap)
            let SRCCOPY = 0x00CC0020u
            let success = BitBlt(hMemDC, 0, 0, width, height, hWndDC, 0, 0, SRCCOPY)
            //BitBlt(hdcMemDC, 0, 0, captureWidth, captureHeight, hdcScreen, adjustedLeft, adjustedTop, SRCCOPY) |> ignore
            let bmp = if success then Image.FromHbitmap(hBitmap) else null
            Win32.SelectObject(hMemDC, hOld) |> ignore
            Win32.DeleteObject(hBitmap) |> ignore
            Win32.DeleteDC(hMemDC) |> ignore
            Win32.ReleaseDC(hWnd, hWndDC) |> ignore
            if success then Some bmp else None    // Replace "notepad" with your target process name
    //captureProcessWindow "notepad"

*)
    open ScreenCapture.NET
    open HPPH

/// Extension method: converts a RefImage<ColorBGRA> to Bitmap
    type IImage with
        member this.ToBitmap32() =
            let width, height = this.Width, this.Height
            let bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb)
            let rect = Rectangle(0, 0, width, height)
            let bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat)
            try
                let mutable destPtr = bmpData.Scan0
                for row in this.Rows do
                    let spanLength = bmpData.Stride / sizeof<ColorBGRA>
                    // Copy BGRA spans directly into bitmap memory
                    let spn = Span<byte>(destPtr.ToPointer(), spanLength)                    
                    row.CopyTo(spn)                    
                    destPtr <- IntPtr(destPtr.ToInt64() + int64 bmpData.Stride)
            finally
                bmp.UnlockBits(bmpData)
            bmp

    let toBitmap (width,height)(bytes:ReadOnlySpan<byte>) =         
        let bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb)
        let rect = Rectangle(0, 0, width, height)
        let bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat)
        try
            let mutable destPtr = bmpData.Scan0
            let destSpan = Span<byte>(destPtr.ToPointer(),bytes.Length)
            bytes.CopyTo(destSpan)
        finally
            bmp.UnlockBits(bmpData)
        bmp

    let screenCapture(hWnd) =
        let mutable rect = Win32.RECT()
        if Win32.GetWindowRect(hWnd, &rect) then
            let width = rect.Right - rect.Left
            let height = rect.Bottom - rect.Top
            let monW,monH = Win32.getMonitorSizeForWindow hWnd |> Option.defaultWith(fun _ -> failwith "no monitor into")
            use sc = new DX11ScreenCaptureService()
            let gcs = sc.GetGraphicsCards()
            let dsps = gcs |> Seq.collect (sc.GetDisplays)
            let dsp1 = dsps |> Seq.filter(fun d -> d.Width = monW && d.Height = monH) |> Seq.tryHead
            match dsp1 with 
            | Some dspW  -> 
                use scap = sc.GetScreenCapture(dspW)
                let capZone = scap.RegisterCaptureZone(rect.Left, rect.Top, width, height)
                scap.CaptureScreen() |> ignore
                use l = capZone.Lock()
                let bytes = capZone.RawBuffer
                let bitmap = toBitmap (width,height) bytes
                Some bitmap
            | None -> None
        else
            None 

    let getPid name = 
        let procs = System.Diagnostics.Process.GetProcessesByName(name)
        if procs.Length = 0 then    
            failwith $"no process found with name '{name}'"
        procs.[0].Id    

    let snapshot (name:string) = async {
        let pid = getPid name
        let handle = Win32.findTopmostWindow pid 
        //let bitmap = handle |> Option.bind captureWindowBitBlt |> Option.defaultWith (fun _ -> failwith "unable to capture snapshot")
        let bitmap = 
            handle 
            |> Option.bind (fun h -> Win32.captureWindow(h)) 
            |> Option.defaultWith (fun _ -> failwith "unable to capture snapshot")
        bitmap.Save(@"c:\s\temp.bmp",ImageFormat.Bmp)//, ImageFormat.Jpeg)
        use ms = new MemoryStream()
        bitmap.Save(ms, ImageFormat.Png)
        let buff = ms.GetBuffer()
        return buff
    }
    
    let doubleClick (x,y) = async {
        let! v = 
            Simulate
                .Events()
                .MoveTo(x,y)
                .DoubleClick(Events.ButtonCode.Left)
                .Invoke() 
                |> Async.AwaitTask
        ()        
    }

    let click (x,y,btn:Events.ButtonCode) = async {
        let! v = 
            Simulate
                .Events()
                .MoveTo(x,y)
                .Click(btn)  
                .Invoke()
                |> Async.AwaitTask
                
        ()        
    }

    let typeText (text:string) = async {        
        let! v = 
            Simulate
                .Events()
                .Click(text)
                .Invoke() 
                |> Async.AwaitTask
        ()
    }

    let wheel (deltaX:int,deltaY:int) = async {
        let! v = 
            Simulate
                .Events()
                .Scroll(Events.ButtonCode.HScroll,deltaX)
                .Scroll(Events.ButtonCode.VScroll,deltaY)
                .Invoke()
                |> Async.AwaitTask
        ()
    }

    let move (x:int,y:int) = async {
        let! v = 
            Simulate
                .Events()
                .MoveTo(x,y)
                .Invoke()
                |> Async.AwaitTask
        ()
    }

    let scroll (x:int, y:int) (scrollX:int, scrollY:int) = async {
        let! v = 
            Simulate
                .Events()
                .MoveTo(x,y)
                .Scroll(Events.ButtonCode.HScroll, scrollX)
                .Scroll(Events.ButtonCode.VScroll, scrollY)
                .Invoke()
                |> Async.AwaitTask
        ()
    }

    let pressKeys (ks:Events.KeyCode[]) = async {
        let! v = 
            Simulate
                .Events()
                .Click(ks)
                .Invoke()
                |> Async.AwaitTask
        ()
    }

    let dragAndDrop (sX:int,sY:int) (tX:int, tY:int) = async {
        let! v =
            let src = Events.MouseMove.Create(sX,sY,Events.MouseOffset.Absolute)
            let tgt = Events.MouseMove.Create(tX,tY,Events.MouseOffset.Absolute)
            Simulate
                .Events()
                .DragDrop(src,tgt)
                .Invoke()
                |> Async.AwaitTask
        ()
    }
 