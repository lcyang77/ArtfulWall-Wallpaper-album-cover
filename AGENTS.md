# AGENT Instructions

This repository contains **ArtfulWall**, a Windows-only wallpaper management tool written in C# for .NET 7 WinForms. The project uses ImageSharp for image processing and includes utilities for multi-monitor and DPI aware wallpaper generation.

## Directory Overview
- `Core/` – application entry point and tray application context.
- `Services/` – background logic such as `WallpaperUpdater` and `ImageManager`.
- `Models/` – configuration classes.
- `UI/` – WinForms configuration editor.
- `Utils/` – Windows specific utilities for interacting with system APIs.
- `config.json` – default runtime configuration file.

## Coding Style
- Use four space indentation.
- Braces open on the same line as declarations.
- Class and method names are PascalCase. Method parameters and local variables are camelCase.
- Private fields begin with `_` and use camelCase.
- Prefer `var` for local variable declarations when the type is obvious.
- Keep existing comments (mostly Chinese) intact when modifying files. New comments may be in English or Chinese but stay concise.

## Programmatic Checks
- Run `dotnet build ARTFULWALL.sln` before committing to ensure the project compiles. No unit tests are provided.
- If the build cannot be executed in the environment, still document the failure in the PR.

## Contribution Notes
- Keep changes in a single commit; do not create new branches.
- Summaries for PRs should mention key features or bug fixes. In the testing section include the `dotnet build` result.

