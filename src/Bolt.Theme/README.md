# Bolt.Theme

> This app's own copy, forked from andrew's aibolt on 2026-10-07 and owned by this repo from then on: change it here
> (no syncing with aibolt). Builds with the repo's Directory.Build.props.

A dark-first Avalonia 12 theme: graphite surfaces, hairline borders, Geist / Geist Mono type and a single accent
color. Light variant included. It has no dependency on aibolt and can be used in any Avalonia app.

## Use it

Reference the project (or package), then replace `<FluentTheme />`:

```xml
<Application xmlns:bolt="using:Bolt.Theme" ...>
    <Application.Styles>
        <bolt:BoltTheme Accent="#7C9BF7" />   <!-- hosts FluentTheme internally -->
    </Application.Styles>
</Application>
```

`Accent` (and `TintedBubbles`) can also be changed at runtime through `BoltTheme.Current`. Every accent-derived
brush is recomputed for both variants, using the same OKLab mixing as the CSS `color-mix()` in the design.

## What's inside

| File | Contents |
|---|---|
| `Tokens.axaml` | `Bolt.Bg.*`, `Bolt.Border.*`, `Bolt.Fg.*`, status colors (per variant), fonts, sizes, radii |
| `BoltTheme.axaml.cs` | accent-derived resources: `Bolt.Accent`, `.Hover`, `.Text`, `.Border`, `.Tint`, `.Selection`, `Bolt.Shadow.FocusRing`, `Bolt.Bubble.Bg/Border` |
| `Icons.axaml` | `Bolt.Icon.*` 16×16 line icons (`*.Fill` ones are meant to be filled) |
| `ControlThemes.axaml` | re-templated `Button`, `ToggleButton`, `CheckBox`, `TabControl`/`TabItem`, `ProgressBar`, `TitleBar` |
| `Styles.axaml` | Fluent-templated controls restyled (`TextBox`, `ComboBox`, `ListBox`, menus, tooltips) and utility classes |
| `Controls/` | `Icon`, `StatusRing`, `Spinner`, `ShareRowPanel`, `TitleBar`, `MarkdownBlock` |
| `Assets/Fonts` | Geist and Geist Mono (SIL OFL 1.1, see `OFL-Geist.txt`) |

Always reference colors via `{DynamicResource Bolt.…}` so the light/dark switch and accent changes apply.

### Class vocabulary

- **Button**: default (raised secondary), `accent`, `ghost`, `icon` (square, no chrome), `row` + `selected`
  (list row), `subtle`, `link`
- **ToggleButton**: default, `pill` (rounded mode chip)
- **TextBox**: `composer` (multi-line chat input with an accent focus ring), `mono`
- **ComboBox**: `compact` (30px toolbar height)
- **TabControl**: default (44px strip), `large` (dialog strip on the chrome surface)
- **TextBlock**: `title h1 h2 caption small tiny mono`, plus colors `strong secondary tertiary muted subtle faint
  fainter ghost accent success danger`
- **Border**: `card`, `badge` (a mono tag), `hairline`, `vrule`, `dot` (6px; set `Background` on the element),
  `bubble` (chat message)

### Controls

```xml
xmlns:b="using:Bolt.Theme.Controls"

<b:Icon Data="{StaticResource Bolt.Icon.Plus}" Size="14" />               <!-- stroked with the inherited Foreground -->
<b:StatusRing Kind="Progress" Fraction="0.5" />                           <!-- Empty · Pending · Progress · Done · Cancelled -->
<b:Spinner Size="12" Foreground="{DynamicResource Bolt.Accent}" />        <!-- rotating arc; animates only while visible -->
<b:ShareRowPanel Spacing="4" MaxItemWidth="360" />                        <!-- flex row; b:ShareRowPanel.Weight on children -->
<b:TitleBar />                                                            <!-- with ExtendClientAreaToDecorationsHint="True" and TitleBarHeightHint 39 -->
<b:MarkdownBlock Markdown="{Binding Text}" />                             <!-- chat-grade markdown: headings, lists, tables, code, links; selectable -->
```

## Gotchas

- Inside a `DataTemplate`, a style setter can override a local `{DynamicResource}` on the same property. That is
  why `.dot` sets only size, and color variants are set on the element or through their own class.
- Grid `ColumnSpacing` still adds a gap next to collapsed columns. Use margins when a column can be hidden.
- Glyphs missing from Geist (★, arrows) fall back to a font with a taller line. Set `LineHeight` on single-line text
  that must keep a fixed row height.
