# How to compile the plugin (no developer setup needed)

Compiling a NINA plugin can only happen on **Windows** (it's WPF/.NET). You do **not**
need to install Visual Studio or .NET yourself — let **GitHub build it in the cloud** on a
free Windows machine and hand you the finished `.dll`.

## Option A — GitHub builds it for you (recommended, zero install)

1. **Make a free GitHub account** at github.com (if you don't have one).
2. **Create a new repository** (green "New" button). Name it e.g. `uas-object-chooser`.
   You can make it private.
3. **Upload the project files.** On the empty repo page click **"uploading an existing
   file"**, then drag in **everything inside the `UAS.ObjectChooser` folder** (all the
   `.cs`, `.xaml`, the `.csproj`, the `Models`/`Services`/`Properties` folders, and the
   `.github` folder). Commit.
   - Important: the `.github/workflows/build.yml` file must come along — that's the build recipe.
4. GitHub automatically starts building. Open the **"Actions"** tab → click the running job.
   After ~1–2 minutes it finishes.
5. If it's **green ✓**: scroll to **"Artifacts"** at the bottom of the run and download
   **`UAS.ObjectChooser-plugin`**. Inside is `UnderAfricanSkies.ObjectChooser.dll`.
6. Copy that DLL to `%localappdata%\NINA\Plugins\UnderAfricanSkies.ObjectChooser\` on your
   imaging PC, start NINA → the **UAS Object Chooser** panel is there.

### If the build is red ✗ (very likely the first time)

That's expected: a handful of NINA API calls are marked `// VERIFY` and may not match your
exact NINA version. The Actions log shows the precise errors. **Copy the red error lines and
paste them to me** — I'll fix them and you re-run (Actions tab → "Re-run jobs"). We iterate
until it's green. I don't need a compiler to do this; the error messages are enough.

## Option B — Build on your own Windows PC

If you'd rather keep it local:

1. Install **Visual Studio 2022 Community** (free) with the *.NET desktop development*
   workload — this includes the .NET 8 SDK.
2. Open `UAS.ObjectChooser.csproj`.
3. Menu **Build → Build Solution** (or press Ctrl+Shift+B).
4. The DLL appears in `bin\x64\Release\net8.0-windows\UnderAfricanSkies.ObjectChooser.dll`.
5. Copy it to `%localappdata%\NINA\Plugins\UnderAfricanSkies.ObjectChooser\`.

Minimal variant: install just the **.NET 8 SDK**, open a terminal in the project folder, and
run `dotnet build -c Release`.

## Why there's no "compile connector" / MCP

WPF (the UI framework NINA plugins use) only builds on Windows, and the build needs NINA's
own assemblies restored from NuGet. No cloud connector or MCP performs that — a real Windows
build environment is required. GitHub Actions (Option A) is exactly that environment, for
free, without anything installed on your side.
