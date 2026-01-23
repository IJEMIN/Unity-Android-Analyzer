# Unity Project Analyzer (v1.0)

A tool for analyzing Unity project build artifacts (APK, OBB) to identify the engine version, render pipeline, major libraries, and technology stack used.

## Key Features

*   **Android Analysis Support**: Analyze local `.apk` / `.obb` files or pull apps directly from a connected device (`adb`) for analysis.
*   **Engine Insight**:
    *   **Unity Version**: Detects Unity 6.x and older versions.
    *   **Render Pipeline**: Detects Built-in, URP, HDRP, and SRP.
*   **Tech Stack & Framework Detection**:
    *   **Entities (DOTS)**: Detects usage via Scenes, ScriptingAssemblies, and RuntimeInitializeOnLoad.
    *   **Physics**: Detects Unity Physics (Entities) and Havok Physics.
    *   **UI Frameworks**: Detects NGUI, UI Toolkit (Runtime), and Addressables.
*   **Scripting Metadata**:
    *   Extracts major namespaces and provides statistics from `global-metadata.dat`.
    *   Provides quick shortcuts to open `global-metadata.dat` and `ScriptingAssemblies.json` immediately after analysis.

## Prerequisites

The following tools must be installed to run the program:

1.  **adb (Android Debug Bridge)**: 
    *   Required for device analysis features.
    *   Must be registered in your system `PATH`.
    *   Can be installed via [Android SDK Platform-Tools](https://developer.android.com/studio/releases/platform-tools).
2.  **.NET 10.0 Runtime**:
    *   While self-contained builds (including the runtime) are possible depending on the environment, installing the latest .NET runtime is generally recommended.

## How to Use

### 1. Local Analysis
*   Select the `.apk` file you wish to analyze and click the **Analyze** button.

### 2. Device Analysis
1.  Connect your Android device via USB or Wireless ADB.
2.  Click the **Refresh** button to update the device list and select your target.
3.  Search for the package name (e.g., `com.company.product`) and select it from the list.
4.  Click the **Analyze** button to extract the necessary data from the device and start the analysis.

### 3. View Results
*   Check the detected version and tech stack on the central dashboard.
*   Click the **Open Metadata** or **Open Assemblies** buttons to view the extracted raw data in your default text editor.

## Supported Platforms

Currently developed and tested primarily on **macOS (Apple Silicon)**.

*   **macOS**: Native support for Apple Silicon (M1/M2/M3).
*   **Linux**: Executable (x64).
*   **Windows**: No official build support or testing at this time. However, since it is based on .NET 10, it may be possible to run it via source build.

## Building from Source (macOS)

To generate a macOS App Bundle (`UnityProjectAnalyzer.app`), use the following command:

```bash
dotnet publish UnityProjectAnalyzer.Gui/UnityProjectAnalyzer.Gui.csproj -c Release -r osx-arm64
```

The output will be generated in the `UnityProjectAnalyzer.Gui/bin/Release/net10.0/osx-arm64/publish/UnityProjectAnalyzer.app` folder.

## License

This project is licensed under the [MIT License](LICENSE).
