# GPU algorithm and interop dependencies

AMD FidelityFX FSR 1: MIT, GPUOpen-Effects/FidelityFX-FSR commit a21ffb8f6c13233ba336352bdff293894c706575. Original headers and full license are retained in Shaders/AMD-LICENSE.txt. Uses the official EASU and RCAS functions.

NVIDIA Image Scaling SDK 1.0.3: MIT, NVIDIAGameWorks/NVIDIAImageScaling commit 35e13ba316c98eeecf16f37eae70ce88019911f6. Original headers, example shader and full license header are retained in Shaders/NVIDIA-LICENSE.txt. Uses NVScaler and the original SDK coefficient tables. SDR configuration constants are ported from NVScalerUpdateConfig.

Vortice.Windows 3.6.2 and Vortice.Mathematics 1.9.2: MIT, https://github.com/amerkoleci/Vortice.Windows . Copyright Amer Koleci and contributors. Direct3D11, DXGI and D3DCompiler bindings. Full license retained in Shaders/Vortice-LICENSE.txt.

SharpGen.Runtime and SharpGen.Runtime.COM 2.2.0-beta: MIT. Copyright (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci. Full upstream license retained in Shaders/SharpGen-LICENSE.txt.

Microsoft Windows SDK .NET 10.0.19041.56: Copyright Microsoft Corporation. Redistribution is subject to the package license: https://aka.ms/WinSDKLicenseURL . WinRT.Runtime is from C#/WinRT, MIT, Copyright Microsoft Corporation; full license retained in Shaders/CsWinRT-LICENSE.txt. No Gamescope compositor, Lossless Scaling DLL or external scaling application is bundled.
