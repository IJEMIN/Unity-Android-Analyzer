# Unity Project Analyzer

C# tool for inspecting Unity builds and apps. Currently supports Android (APK / OBB) and apps installed on a connected device.
Outputs all results in Markdown format.

## Features

* Analyze local APK / OBB files
* Pull APK / OBB from an Android device via adb
* Detect
  * Unity Engine Version (supports Unity 6.x and older)
  * Render Pipeline (Built-in / URP / HDRP)
  * Entities usage (via ScriptingAssemblies.json / RuntimeInitializeOnLoads.json)
  * Addressables usage
  * Havok Physics usage
  * Major namespaces found in global-metadata.dat

## Requirements
* .net 9.0
* adb available in PATH
* Windows, macOS, or Linux
