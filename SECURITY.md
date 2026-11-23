# Security Policy

## Reporting a Vulnerability

If you discover a security issue in RuleOpen (for example: argument/command injection in dispatcher launching, unsafe path resolution, or privilege boundary issues), please **do not** open a public GitHub issue with exploit details.

Instead, report it privately via GitHub Security Advisories on the repository:
`https://github.com/Paul1708/RuleOpen/security/advisories/new`

Please include:
- A clear description of the issue and affected component (`RuleOpen.Dispatcher`, `RuleOpen.ShellExtension`, `RuleOpen.Windows`, etc.)
- Steps to reproduce or proof-of-concept inputs
- Potential impact

## Security Design Principles

- RuleOpen runs exclusively under standard user privileges (`asInvoker`) and requires no administrator access.
- File paths and target executables are treated as untrusted input and launched via Shell COM APIs (`IAssocHandler`, `IApplicationActivationManager`) or `ProcessStartInfo.ArgumentList` without shell command-string interpolation.
- RuleOpen works 100% offline with zero network calls or telemetry.
