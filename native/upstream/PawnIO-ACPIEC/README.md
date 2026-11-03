# PawnIO ACPI EC dependency

Unmodified `LpcACPIEC.bin` from namazso/PawnIO.Modules release **0.2.11**.

- Release: https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11
- Release ZIP SHA256: `43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4`
- Module SHA256: `c38fd116e7aff4d1fdb0a494e296be0a6708e5a22fc72f14587442fb7f8f7906`
- Source snapshot: https://github.com/namazso/PawnIO.Modules/blob/24425a5299e84410751da882c121f322fb030b82/LpcACPIEC.p
- License: LGPL-2.1-or-later; see `COPYING` and the module source.

The signed module permits only ACPI EC ports 0x62/0x66. The toolbox's own C# host
uses the Access_EC mutex and only exposes the two audited CG3EM fan outputs.
No InpOut or WinRing0 driver is installed by this integration.
`tools/Prepare-FanModule.ps1` fetches and verifies the release, then copies the
module and license into build output. The service checks the module digest too.
