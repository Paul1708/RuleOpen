# Contributing to RuleOpen

Thank you for your interest in contributing to **RuleOpen**!

## Development Prerequisites

- Windows 11 (or Windows 10 19041+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022 / 2026 (or Build Tools) with:
  - **.NET Desktop Development**
  - **Windows App SDK / WinUI 3** components
  - **Desktop development with C++** (for `RuleOpen.ShellExtension.dll`)
- Windows 10/11 SDK (`10.0.26100.0`)

## Building Locally

```powershell
# Build C++ Explorer context menu extension
msbuild src\RuleOpen.ShellExtension\RuleOpen.ShellExtension.vcxproj /p:Configuration=Release /p:Platform=x64

# Build managed solution
dotnet build RuleOpen.sln -c Release -p:Platform=x64

# Run all automated tests
dotnet test tests\RuleOpen.Core.Tests\RuleOpen.Core.Tests.csproj -c Release
dotnet test tests\RuleOpen.Integration.Tests\RuleOpen.Integration.Tests.csproj -c Release -p:Platform=x64
```

## Coding Guidelines

- Follow `.editorconfig` formatting and keep nullable reference types (`Nullable=enable`) warning-free.
- Keep `RuleOpen.Core` strictly free of Windows-specific APIs so the rule engine remains 100% unit-testable.
- Never silently modify Windows `UserChoice` file associations or bypass OS user-choice protections.
- Keep UI changes aligned with native WinUI 3 Fluent Design controls and theme brushes (no hardcoded light/dark colors).
- Add unit tests in `RuleOpen.Core.Tests` for any change to normalization, precedence, config persistence, or dispatch logic.

## Pull Request Checklist

1. Ensure `dotnet build` and `dotnet test` pass cleanly.
2. Test both Light and Dark themes if you modified XAML views.
3. Update relevant documentation (`docs/` or `README.md`) when adding or changing user-facing behavior.
4. Keep commits focused with clear, descriptive messages.
