# Visual Pass — reduce branch (Windows 11 identity, Phenix/Sahli models, Explorer shell)

Owner directives (2026-11-24): windows must resemble Windows 11 system
windows (File Explorer / Settings); invoices + vouchers modelled on
PHENIX ERP v10; reports modelled on SAHLI SOFT; MWin shell converted to
the Explorer-style command bar + navigation pane. All changes go through
the existing central files only (Themes/VisuId.xaml, App.xaml,
Views/BaseWin.cs, Services/VisuId.cs, Services/Win11Chrome.cs) plus the
in-scope windows themselves. Visual/rendering verification is
**Windows-owed**: this document records design decisions and code
changes, never a claim that the rendered look was verified here.

## V1 — Central Windows 11 pass (commit: visual V1)

State found: the central system was already token-based and largely
Win11-shaped — DWM rounded corners + Mica (Win11Chrome), 32px slim
caption with 46px caption buttons, 4/8px radius scale, Segoe UI
Variable, red close-hover (#C42B1C, the exact WinUI value). The gaps
were identity values left from the pre-Win11 slate theme:

1. **Accent** — the palettes were Material Design colors and the
   default accent was slate gray (#607D8B). Added a `win11` palette
   (Primary #0067C0, Dark #005A9E, Light #E5F1FB, Hover #D6EBF9 —
   the Windows 11 default accent family) as the lead palette in
   Services/VisuId.cs, and made it the default/fallback accent
   everywhere in that file. The XAML accent tokens in
   Themes/VisuId.xaml carry the same values so first paint matches.
   A user-saved accent choice is untouched (a saved "gray" still
   applies). **Flagged follow-up (outside this pass's file list):**
   `Configuration/UsrPref.cs` still defaults `AccentColor` to
   `"gray"`, so a profile that never stored a choice reads "gray"
   back; flipping that one default to `"win11"` is a one-line change
   awaiting the owner's word.
2. **Title bar** — was slate (#607D8B) with white text, the pre-Win11
   identity. Now the bar takes the client surface color
   (ChromeBackgroundBrush = Neutral100, the window BackgroundBrush)
   with Neutral900 text (ChromeForegroundBrush), the File Explorer /
   Settings arrangement; the hairline under the bar and around the
   window is Neutral300 (ChromeBorderBrush). Caption-button hover /
   pressed are neutral subtle fills (#E8EAED / #D5D9DE); the close
   button keeps its WinUI red hover. MWin.xaml consumes the same two
   chrome tokens in paired background/foreground, so the shell stays
   coherent until its own conversion phase.
3. **Interaction fills** — hover/pressed surfaces lightened one
   neutral step (SurfaceHoverBrush 200→150, SurfacePressedBrush
   250→200) toward Win11's subtle-fill values; selection/subtle
   accent brushes re-hued to the Win11 accent at the same alpha.
4. **Backdrop layering** — Win11Chrome's documented intent ("Mica Alt
   suits dense ERP dialogs") was never wired. BaseWin now applies it
   on load: the shell (ownerless) keeps the token backdrop (Mica);
   every owned secondary window gets Mica Alt, the transient-window
   material Windows 11 uses for dialogs. Best-effort, no-ops on
   older DWM.
5. **Grid hairlines** — TableHeaderBorderBrush slate → Neutral300;
   the opening-stock header tint re-hued to the accent dark (#005A9E);
   PrimaryTransparentBrush re-hued at the same alpha.
6. **App.xaml** — its app-level chrome button style now points at the
   central ChromeButtonHover/Pressed tokens instead of ribbon/border
   brushes, so every chrome surface shares one state palette.

No resource keys were removed or renamed; no window file was touched
in V1. RTL, shell-context inheritance, the 50% opening cap and the
bottom-button convention are unaffected (token values only).

## V2 — Phenix v10 invoices/vouchers + Sahli Soft reports

(Research notes and implementation record appended when the phase runs.)

## V3 — MWin Explorer-style shell conversion

(Record appended when the phase runs.)
