# Design language

Every colour, radius, font size and padding comes from `src/VmQontrol.App/Theme/Tokens.axaml`, defined for both the
dark and the light theme. Views pick a named style from `Theme/Styles.axaml` (`hero`, `h1`, `section`, `muted`,
`card`, `primary`, `secondary`, `search`) and never set a colour of their own.

- **Accent** is the logo's blue. It marks the one primary action on a page and the current selection, nothing else.
- **Surfaces** are cool slate, so the machines' own screens carry the colour.
- **Status colours** (success, warning, danger) are reserved for machine state and validation.
- **Words before widgets.** Every setting has a label, one sentence of what it does, the recommended value, and a
  badge saying when it takes effect.

`VMQONTROL_THEME=dark|light` pins the theme for screenshots; otherwise the desktop's preference is followed.
