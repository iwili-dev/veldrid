using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Veldrid.D3D11
{
    internal class D3D11Swapchain : Swapchain
    {
        public override Framebuffer Framebuffer => framebuffer;

        public PresentFlags PresentFlags
        {
            get
            {
                if (allowTearing && canTear && !classicExclusive && !SyncToVerticalBlank)
                    return PresentFlags.AllowTearing;

                return PresentFlags.None;
            }
        }

        public IDXGISwapChain DxgiSwapChain { get; private set; }

        /// <summary>
        /// Whether this swapchain was last successfully switched into DXGI exclusive fullscreen by <see cref="SetExclusiveFullscreen"/>.
        /// </summary>
        public bool IsExclusiveFullscreen { get; private set; }

        /// <summary>
        /// While true, the swapchain is (re)created in the classic bitblt model (SwapEffect.Discard + AllowModeSwitch),
        /// which is the model DXGI exclusive fullscreen was designed for, instead of the flip model.
        /// </summary>
        private bool classicExclusive;

        /// <summary>
        /// Human-readable details about the last <see cref="SetExclusiveFullscreen"/> attempt (for logging).
        /// </summary>
        public string LastExclusiveInfo { get; private set; } = string.Empty;

        public int SyncInterval { get; private set; }

        public override bool IsDisposed => disposed;

        public override string Name
        {
            get
            {
                unsafe
                {
                    byte* pname = stackalloc byte[1024];
                    int size = 1024 - 1;
                    DxgiSwapChain.GetPrivateData(CommonGuid.DebugObjectName, ref size, new IntPtr(pname));
                    pname[size] = 0;
                    return Marshal.PtrToStringAnsi(new IntPtr(pname));
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                    DxgiSwapChain.SetPrivateData(CommonGuid.DebugObjectName, 0, IntPtr.Zero);
                else
                {
                    IntPtr namePtr = Marshal.StringToHGlobalAnsi(value);
                    DxgiSwapChain.SetPrivateData(CommonGuid.DebugObjectName, value.Length, namePtr);
                    Marshal.FreeHGlobal(namePtr);
                }
            }
        }

        public override bool SyncToVerticalBlank
        {
            get => vsync;
            set
            {
                vsync = value;
                SyncInterval = D3D11Util.GetSyncInterval(value);
            }
        }

        // Exclusive fullscreen is driven from here, so that an unmodified osu!framework works: it sets AllowTearing = (window mode == Fullscreen).
        // On by default; set the environment variable OSU_D3D11_EXCLUSIVE=0 to disable.
        private static readonly bool automaticExclusive = Environment.GetEnvironmentVariable("OSU_D3D11_EXCLUSIVE") != "0";

        // Experimental, off by default: stay in exclusive fullscreen while a window on ANOTHER monitor is active (OBS, chat, browser on a second screen),
        // so clicking there does not make the game screen blink. Only used while "Minimise osu! when switching to another app" is off.
        // Set the environment variable OSU_D3D11_KEEP_ON_OTHER_MONITOR=1 to enable.
        private static readonly bool keepOnOtherMonitor = Environment.GetEnvironmentVariable("OSU_D3D11_KEEP_ON_OTHER_MONITOR") == "1";

        // Give up after this many failed attempts in a row (a success resets the counter, so many Alt+Tabs over a session do not add up).
        private const int max_automatic_failures = 3;
        private const int retry_delay_ms = 500;
        private const int state_check_interval_ms = 100;

        // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE: usually temporary (another app or overlay holds the output), so it gets a longer budget.
        private const int dxgi_error_not_currently_available = unchecked((int)0x887A0022);
        private const int max_not_available_failures = 20;

        private bool exclusiveWanted;
        private int automaticFailures;
        private int notAvailableFailures;
        private long lastExclusiveAttemptTicks;
        private long lastStateCheckTicks;

        public bool AllowTearing
        {
            get => allowTearing || exclusiveWanted;
            set
            {
                if (automaticExclusive && automaticFailures < max_automatic_failures && description.Source is Win32SwapchainSource)
                {
                    exclusiveWanted = value;

                    // leaving the Fullscreen window mode: leave exclusive and go back to the flip-model swapchain.
                    if (!value && (IsExclusiveFullscreen || classicExclusive))
                        SetExclusiveFullscreen(false, 0, 0, 0, 0);

                    return;
                }

                if (allowTearing == value)
                    return;

                allowTearing = value;

                if (!canTear)
                    return;

                recreateSwapchain();
            }
        }

        private readonly D3D11GraphicsDevice gd;
        private readonly SwapchainDescription description;
        private readonly PixelFormat? depthFormat;

        private readonly object referencedCLsLock = new object();

        private readonly bool canTear;
        private readonly bool canCreateFrameLatencyWaitableObject;
        private readonly Format colorFormat;
        private bool vsync;
        private D3D11Framebuffer framebuffer;
        private D3D11Texture depthTexture;
        private float pixelScale = 1f;
        private SwapChainFlags flags;
        private bool disposed;
        private FrameLatencyWaitHandle frameLatencyWaitHandle;
        private readonly HashSet<D3D11CommandList> referencedCLs = new HashSet<D3D11CommandList>();

        private bool allowTearing;

        private uint width;
        private uint height;

        private ID3D11Texture2D backBufferTexture;

        public D3D11Swapchain(D3D11GraphicsDevice gd, ref SwapchainDescription description)
        {
            this.gd = gd;
            this.description = description;
            depthFormat = description.DepthFormat;
            SyncToVerticalBlank = description.SyncToVerticalBlank;

            colorFormat = description.ColorSrgb
                ? Format.B8G8R8A8_UNorm_SRgb
                : Format.B8G8R8A8_UNorm;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory5 = this.gd.Adapter.GetParent(out IDXGIFactory5 f).Success ? f : null)
                canTear = dxgiFactory5?.PresentAllowTearing == true;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory3 = this.gd.Adapter.GetParent(out IDXGIFactory3 f).Success ? f : null)
                canCreateFrameLatencyWaitableObject = dxgiFactory3 != null;

            width = description.Width;
            height = description.Height;

            recreateSwapchain();
        }

        #region Disposal

        public override void Dispose()
        {
            if (!disposed)
            {
                // DXGI does not allow releasing a swapchain that is still in fullscreen.
                if (IsExclusiveFullscreen || isReallyFullscreen())
                {
                    try
                    {
                        DxgiSwapChain?.SetFullscreenState(false, null);
                    }
                    catch (Exception)
                    {
                    }

                    IsExclusiveFullscreen = false;
                }

                depthTexture?.Dispose();
                framebuffer.Dispose();
                DxgiSwapChain.Dispose();

                disposed = true;
            }
        }

        #endregion

        public override void Resize(uint width, uint height)
        {
            this.width = width;
            this.height = height;

            lock (referencedCLsLock)
            {
                foreach (var cl in referencedCLs) cl.Reset();

                referencedCLs.Clear();
            }

            bool resizeBuffers = false;

            if (framebuffer != null)
            {
                resizeBuffers = true;
                depthTexture?.Dispose();

                backBufferTexture.Dispose();
                framebuffer.Dispose();
            }

            uint actualWidth = (uint)(width * pixelScale);
            uint actualHeight = (uint)(height * pixelScale);
            if (resizeBuffers) DxgiSwapChain.ResizeBuffers(2, (int)actualWidth, (int)actualHeight, colorFormat, flags).CheckError();

            // Get the backbuffer from the swapchain
            backBufferTexture = DxgiSwapChain.GetBuffer<ID3D11Texture2D>(0);

            if (depthFormat != null)
            {
                var depthDesc = new TextureDescription(
                    actualWidth, actualHeight, 1, 1, 1,
                    depthFormat.Value,
                    TextureUsage.DepthStencil,
                    TextureType.Texture2D);
                depthTexture = new D3D11Texture(gd.Device, ref depthDesc);
            }

            var backBufferVdTexture = new D3D11Texture(
                backBufferTexture,
                TextureType.Texture2D,
                D3D11Formats.ToVdFormat(colorFormat));

            var desc = new FramebufferDescription(depthTexture, backBufferVdTexture);
            framebuffer = new D3D11Framebuffer(gd.Device, ref desc)
            {
                Swapchain = this
            };
        }

        /// <summary>
        /// Switches the swapchain in or out of DXGI exclusive fullscreen.
        /// For the duration of exclusive fullscreen the swapchain is recreated in the classic (bitblt) model, because DXGI
        /// rejects <c>SetFullscreenState</c> on the flip-model swapchain used otherwise. Tearing is not used while exclusive
        /// (exclusive fullscreen can present without vsync on its own).
        /// Leaving through this method also goes back to the flip-model swapchain.
        /// </summary>
        /// <returns>An HRESULT. Negative values are failures.</returns>
        public int SetExclusiveFullscreen(bool fullscreen, int targetWidth, int targetHeight, int refreshNumerator, int refreshDenominator)
        {
            if (disposed || DxgiSwapChain == null)
                return unchecked((int)0x80004005); // E_FAIL

            if (fullscreen)
                return enterExclusive(targetWidth, targetHeight, refreshNumerator, refreshDenominator);

            int hr = leaveExclusive();

            if (classicExclusive)
            {
                // go back to the regular flip-model swapchain.
                classicExclusive = false;
                recreateSwapchain();
            }

            return hr;
        }

        private int enterExclusive(int targetWidth, int targetHeight, int refreshNumerator, int refreshDenominator)
        {
            try
            {
                // switch to the classic model once. While the window is only alt-tabbed out it stays in that model,
                // so Alt+Tab does not recreate the swapchain (fast, and OBS game capture keeps its hook).
                if (allowTearing || !classicExclusive)
                {
                    allowTearing = false;
                    classicExclusive = true;
                    recreateSwapchain();
                }

                updateWindowAssociation();

                // null output = the output that contains the window. The display is already in the wanted mode (set through SDL).
                var result = DxgiSwapChain.SetFullscreenState(true, null);

                if (result.Failure)
                {
                    // stay in the classic model, the next attempt only has to call SetFullscreenState again.
                    IsExclusiveFullscreen = false;
                    LastExclusiveInfo = $"SetFullscreenState(true) failed with 0x{result.Code:X8}";
                    return result.Code;
                }

                IsExclusiveFullscreen = true;

                var mode = new ModeDescription(targetWidth, targetHeight, new Rational(refreshNumerator, refreshDenominator), colorFormat);
                result = DxgiSwapChain.ResizeTarget(mode);

                // DXGI expects ResizeBuffers after every fullscreen transition.
                resizeAfterTransition();

                LastExclusiveInfo = result.Failure
                    ? $"entered fullscreen (ResizeTarget failed with 0x{result.Code:X8}, ignored)"
                    : "entered fullscreen";

                return 0;
            }
            catch (Exception)
            {
                IsExclusiveFullscreen = isReallyFullscreen();
                LastExclusiveInfo = "entering fullscreen threw an exception";
                return unchecked((int)0x80004005);
            }
        }

        /// <summary>
        /// Leaves exclusive fullscreen but keeps the classic-model swapchain (used when the window loses focus).
        /// </summary>
        private int leaveExclusive()
        {
            if (!IsExclusiveFullscreen && !isReallyFullscreen())
                return 0;

            IsExclusiveFullscreen = false;

            try
            {
                var result = DxgiSwapChain.SetFullscreenState(false, null);
                resizeAfterTransition();

                LastExclusiveInfo = result.Failure ? $"SetFullscreenState(false) failed with 0x{result.Code:X8}" : "left fullscreen";
                return result.Failure ? result.Code : 0;
            }
            catch (Exception)
            {
                LastExclusiveInfo = "leaving fullscreen threw an exception";
                return unchecked((int)0x80004005);
            }
        }

        /// <summary>
        /// Called once per frame right before presenting. Enters exclusive fullscreen when it is wanted and the window is in the foreground,
        /// and leaves it when it is not wanted or the window lost focus.
        /// </summary>
        /// <returns>Whether the swapchain was changed (in which case this frame should not be presented).</returns>
        internal bool UpdateExclusiveFullscreen()
        {
            if (!exclusiveWanted && !IsExclusiveFullscreen && !classicExclusive)
                return false;

            if (disposed || !(description.Source is Win32SwapchainSource win32Source))
                return false;

            IntPtr hwnd = win32Source.Hwnd;
            long now = Environment.TickCount64;

            // Windows or DXGI can drop exclusive fullscreen without us (Alt+Tab while the game minimises and stops drawing,
            // Ctrl+Alt+Del, UAC prompts, ...). Without this check the game believed it was still exclusive and never went back.
            if (IsExclusiveFullscreen && now - lastStateCheckTicks >= state_check_interval_ms)
            {
                lastStateCheckTicks = now;

                if (!isReallyFullscreen())
                {
                    IsExclusiveFullscreen = false;
                    resizeAfterTransition();
                    LastExclusiveInfo = "exclusive fullscreen was lost, will enter again when focused";
                    return true;
                }
            }

            if (!exclusiveWanted)
            {
                SetExclusiveFullscreen(false, 0, 0, 0, 0);
                return true;
            }

            bool focused = GetForegroundWindow() == hwnd && !IsIconic(hwnd);

            if (focused)
            {
                if (IsExclusiveFullscreen)
                    return false;

                if (now - lastExclusiveAttemptTicks < retry_delay_ms)
                    return false;

                lastExclusiveAttemptTicks = now;

                getRefreshRate(hwnd, out int numerator, out int denominator);

                var framebufferBefore = framebuffer;
                int hr = enterExclusive((int)(width * pixelScale), (int)(height * pixelScale), numerator, denominator);

                bool giveUp = false;

                if (hr >= 0)
                {
                    automaticFailures = 0;
                    notAvailableFailures = 0;
                }
                else if (hr == dxgi_error_not_currently_available)
                    giveUp = ++notAvailableFailures >= max_not_available_failures;
                else
                    giveUp = ++automaticFailures >= max_automatic_failures;

                if (giveUp)
                {
                    // give up and behave like before (tearing-enabled "optimised" fullscreen).
                    exclusiveWanted = false;
                    classicExclusive = false;
                    allowTearing = true;
                    recreateSwapchain();
                }

                // a failed retry does not touch the swapchain, so that frame can still be shown.
                return framebuffer != framebufferBefore;
            }

            if (IsExclusiveFullscreen && !canStayExclusiveWhileUnfocused(hwnd))
            {
                leaveExclusive();
                return true;
            }

            return false;
        }

        private bool isReallyFullscreen()
        {
            if (DxgiSwapChain == null)
                return false;

            try
            {
                var result = DxgiSwapChain.GetFullscreenState(out var fullscreen, out IDXGIOutput output);
                output?.Dispose();
                return result.Success && fullscreen;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Recreates the back buffers after a fullscreen transition. Falls back to recreating the swapchain if that fails.
        /// </summary>
        private void resizeAfterTransition()
        {
            try
            {
                Resize(width, height);
            }
            catch (Exception)
            {
                if (isReallyFullscreen())
                {
                    try
                    {
                        DxgiSwapChain.SetFullscreenState(false, null);
                    }
                    catch (Exception)
                    {
                    }
                }

                IsExclusiveFullscreen = false;
                recreateSwapchain();
            }
        }

        private void updateWindowAssociation()
        {
            if (!(description.Source is Win32SwapchainSource win32Source))
                return;

            // IgnoreAll (DXGI_MWA_NO_WINDOW_CHANGES) stops DXGI from leaving fullscreen by itself when the window loses focus.
            // Only used in the experimental mode, and only when the game does not minimise (a minimised game stops drawing and could not leave by itself).
            var flags = WindowAssociationFlags.IgnoreAltEnter;
            if (keepOnOtherMonitor && !minimiseOnFocusLoss())
                flags |= WindowAssociationFlags.IgnoreAll;

            try
            {
                using (var dxgiFactory = gd.Adapter.GetParent<IDXGIFactory>())
                    dxgiFactory.MakeWindowAssociation(win32Source.Hwnd, flags);
            }
            catch (Exception)
            {
            }
        }

        private static bool canStayExclusiveWhileUnfocused(IntPtr hwnd)
        {
            if (!keepOnOtherMonitor || IsIconic(hwnd) || minimiseOnFocusLoss())
                return false;

            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;

            IntPtr gameMonitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            IntPtr otherMonitor = MonitorFromWindow(foreground, 0); // MONITOR_DEFAULTTONULL

            return otherMonitor != IntPtr.Zero && otherMonitor != gameMonitor;
        }

        /// <summary>
        /// Reads osu!'s "Minimise osu! when switching to another app" setting (osu!framework passes it to SDL as a hint).
        /// Unknown counts as on, which is the safe choice.
        /// </summary>
        private static bool minimiseOnFocusLoss()
        {
            try
            {
                IntPtr value = SDL_GetHint("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS");
                if (value == IntPtr.Zero)
                    return true;

                string text = Marshal.PtrToStringUTF8(value);
                return text != "0" && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return true;
            }
        }

        [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        private static void getRefreshRate(IntPtr hwnd, out int numerator, out int denominator)
        {
            numerator = 0;
            denominator = 0;

            try
            {
                IntPtr monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfoW(monitor, ref info)) return;

                byte[] devMode = new byte[220]; // DEVMODEW
                BitConverter.GetBytes((ushort)220).CopyTo(devMode, 68); // dmSize
                if (!EnumDisplaySettingsW(info.szDevice, -1, devMode)) return; // ENUM_CURRENT_SETTINGS

                int hz = BitConverter.ToInt32(devMode, 184); // dmDisplayFrequency
                if (hz > 1)
                {
                    numerator = hz * 1000;
                    denominator = 1000;
                }
            }
            catch (Exception)
            {
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int cbSize;
            public int left, top, right, bottom;
            public int workLeft, workTop, workRight, workBottom;
            public uint flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, byte[] lpDevMode);

        public void WaitForNextFrameReady()
        {
            frameLatencyWaitHandle?.WaitOne(1000);
        }

        public void AddCommandListReference(D3D11CommandList cl)
        {
            lock (referencedCLsLock) referencedCLs.Add(cl);
        }

        public void RemoveCommandListReference(D3D11CommandList cl)
        {
            lock (referencedCLsLock) referencedCLs.Remove(cl);
        }

        private void recreateSwapchain()
        {
            DxgiSwapChain?.Release();
            DxgiSwapChain?.Dispose();
            DxgiSwapChain = null;

            framebuffer?.Dispose();
            framebuffer = null;

            depthTexture?.Dispose();
            depthTexture = null;

            frameLatencyWaitHandle?.Dispose();
            frameLatencyWaitHandle = null;

            // FlipDiscard is only supported on DXGI 1.4+
            bool canUseFlipDiscard;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory4 = gd.Adapter.GetParent(out IDXGIFactory4 f).Success ? f : null)
                canUseFlipDiscard = dxgiFactory4 != null;

            if (classicExclusive)
                canUseFlipDiscard = false;

            var swapEffect = canUseFlipDiscard ? SwapEffect.FlipDiscard : SwapEffect.Discard;

            flags = SwapChainFlags.None;

            if (allowTearing && canTear && !classicExclusive)
                flags |= SwapChainFlags.AllowTearing;
            else if (canCreateFrameLatencyWaitableObject && canUseFlipDiscard)
                flags |= SwapChainFlags.FrameLatencyWaitableObject;

            if (classicExclusive)
                flags |= SwapChainFlags.AllowModeSwitch;

            if (description.Source is Win32SwapchainSource win32Source)
            {
                var dxgiScDesc = new SwapChainDescription
                {
                    BufferCount = 2,
                    Windowed = true,
                    BufferDescription = new ModeDescription(
                        (int)width, (int)height, colorFormat),
                    OutputWindow = win32Source.Hwnd,
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = swapEffect,
                    BufferUsage = Usage.RenderTargetOutput,
                    Flags = flags
                };

                using (var dxgiFactory = gd.Adapter.GetParent<IDXGIFactory>())
                {
                    DxgiSwapChain = dxgiFactory.CreateSwapChain(gd.Device, dxgiScDesc);
                    dxgiFactory.MakeWindowAssociation(win32Source.Hwnd, classicExclusive && keepOnOtherMonitor && !minimiseOnFocusLoss()
                        ? WindowAssociationFlags.IgnoreAltEnter | WindowAssociationFlags.IgnoreAll
                        : WindowAssociationFlags.IgnoreAltEnter);
                }
            }
            else if (description.Source is UwpSwapchainSource uwpSource)
            {
                pixelScale = uwpSource.LogicalDpi / 96.0f;

                // Properties of the swap chain
                var swapChainDescription = new SwapChainDescription1
                {
                    AlphaMode = AlphaMode.Ignore,
                    BufferCount = 2,
                    Format = colorFormat,
                    Height = (int)(height * pixelScale),
                    Width = (int)(width * pixelScale),
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipSequential,
                    BufferUsage = Usage.RenderTargetOutput,
                    Flags = flags
                };

                // Get the Vortice.DXGI factory automatically created when initializing the Direct3D device.
                using (var dxgiFactory = gd.Adapter.GetParent<IDXGIFactory2>())
                {
                    // Create the swap chain and get the highest version available.
                    using (var swapChain1 = dxgiFactory.CreateSwapChainForComposition(gd.Device, swapChainDescription)) DxgiSwapChain = swapChain1.QueryInterface<IDXGISwapChain2>();
                }

                var co = new ComObject(uwpSource.SwapChainPanelNative);

                var swapchainPanelNative = co.QueryInterfaceOrNull<ISwapChainPanelNative>();

                if (swapchainPanelNative != null)
                    swapchainPanelNative.SetSwapChain(DxgiSwapChain);
                else
                {
                    var bgPanelNative = co.QueryInterfaceOrNull<ISwapChainBackgroundPanelNative>();
                    if (bgPanelNative != null) bgPanelNative.SetSwapChain(DxgiSwapChain);
                }
            }

            if ((flags & SwapChainFlags.FrameLatencyWaitableObject) > 0)
            {
                using (var swapChain2 = DxgiSwapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
                {
                    if (swapChain2 != null)
                    {
                        swapChain2.MaximumFrameLatency = 1;
                        frameLatencyWaitHandle = new FrameLatencyWaitHandle(swapChain2.FrameLatencyWaitableObject);
                    }
                }
            }

            Resize(width, height);
        }

        private class FrameLatencyWaitHandle : WaitHandle
        {
            public FrameLatencyWaitHandle(IntPtr ptr)
            {
                SafeWaitHandle = new SafeWaitHandle(ptr, true);
            }
        }
    }
}
