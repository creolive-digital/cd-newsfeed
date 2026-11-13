---
name: Obsidian Cyan Feed
colors:
  surface: '#0b1326'
  surface-dim: '#0b1326'
  surface-bright: '#31394d'
  surface-container-lowest: '#060e20'
  surface-container-low: '#131b2e'
  surface-container: '#171f33'
  surface-container-high: '#222a3d'
  surface-container-highest: '#2d3449'
  on-surface: '#dae2fd'
  on-surface-variant: '#bcc9cd'
  inverse-surface: '#dae2fd'
  inverse-on-surface: '#283044'
  outline: '#869397'
  outline-variant: '#3d494c'
  surface-tint: '#4cd7f6'
  primary: '#4cd7f6'
  on-primary: '#003640'
  primary-container: '#06b6d4'
  on-primary-container: '#00424f'
  inverse-primary: '#00687a'
  secondary: '#d0bcff'
  on-secondary: '#3c0091'
  secondary-container: '#571bc1'
  on-secondary-container: '#c4abff'
  tertiary: '#b4c5ff'
  on-tertiary: '#002a78'
  tertiary-container: '#85a3ff'
  on-tertiary-container: '#003490'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#acedff'
  primary-fixed-dim: '#4cd7f6'
  on-primary-fixed: '#001f26'
  on-primary-fixed-variant: '#004e5c'
  secondary-fixed: '#e9ddff'
  secondary-fixed-dim: '#d0bcff'
  on-secondary-fixed: '#23005c'
  on-secondary-fixed-variant: '#5516be'
  tertiary-fixed: '#dbe1ff'
  tertiary-fixed-dim: '#b4c5ff'
  on-tertiary-fixed: '#00174b'
  on-tertiary-fixed-variant: '#003ea8'
  background: '#0b1326'
  on-background: '#dae2fd'
  surface-variant: '#2d3449'
typography:
  display-lg:
    fontFamily: Inter
    fontSize: 40px
    fontWeight: '700'
    lineHeight: 52px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Inter
    fontSize: 32px
    fontWeight: '700'
    lineHeight: 42px
    letterSpacing: -0.02em
  headline-lg-mobile:
    fontFamily: Inter
    fontSize: 26px
    fontWeight: '700'
    lineHeight: 36px
    letterSpacing: -0.01em
  headline-md:
    fontFamily: Inter
    fontSize: 22px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.01em
  headline-sm:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 26px
  body-lg:
    fontFamily: Inter
    fontSize: 16px
    fontWeight: '400'
    lineHeight: 26px
  body-md:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 22px
  body-sm:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 18px
  label-md:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '500'
    lineHeight: 18px
    letterSpacing: 0.01em
  label-sm:
    fontFamily: Inter
    fontSize: 11px
    fontWeight: '600'
    lineHeight: 14px
    letterSpacing: 0.04em
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  gutter: 1rem
  gutter-lg: 1.5rem
  margin: 1rem
  margin-md: 1.5rem
  margin-lg: 2.5rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 1rem
  space-lg: 1.5rem
  space-xl: 2rem
---

## Brand & Style

This design system delivers an advanced, high-density AI and technology feed experience tailored for modern bilingual (Sinhala and English) digital discourse. The brand personality balances technical precision, clarity, and forward-looking momentum. It targets technologists, researchers, developers, and digital creators who require rapid information scanning without visual fatigue.

The design movement blends **Modern Utilitarian Architecture** with refined **Glassmorphism**. Deep obsidian and slate surfaces establish high signal-to-noise contrast, while luminescent cyan and electric violet accents provide tactical feedback and status demarcation. Visual complexity is minimized to ensure complex multilingual typography—especially Sinhala script combinations—remains effortless to read across long browsing sessions.

## Colors

The palette operates in a default dark mode, engineered around an obsidian bedrock (`#090d16` base canvas, `#0f172a` neutral surface). 

- **Primary (`#06b6d4` Cyan)**: Signals active interaction points, live transmission indicators, real-time metrics, and verified credentials.
- **Secondary (`#8b5cf6` Electric Violet)**: Reserved for generative AI features, algorithmic badges, insight summarization chips, and special user designations.
- **Tertiary (`#2563eb` Vivid Blue)**: Powers foundational structural anchors, link treatments, and state indicators.
- **Neutral Stack**: 
  - Canvas Base: `#090d16`
  - Surface Card (Glass): `rgba(15, 23, 42, 0.72)`
  - Surface Elevated: `#1e293b`
  - Text Primary: `#f8fafc`
  - Text Secondary: `#94a3b8`
  - Border Subdued: `rgba(148, 163, 184, 0.12)`
  - Border Accent: `rgba(6, 182, 212, 0.35)`

Cyan accents must be deployed with restraint to avoid sensory overload. Ambient glow fills behind active elements should remain strictly under `15%` opacity.

## Typography

Typography is calibrated to support seamless switching and co-existence between Latin scripts and complex Sinhala ligatures. `Inter` serves as the primary system driver, paired at runtime with `Noto Sans Sinhala` for vernacular fallback and rendering.

To support Sinhala's vertical diacritics (kombuva, aepilla, ispilla), line-height multipliers are expanded by 10-15% relative to conventional English UI patterns. Text should not drop below 12px for body content to preserve glyph clarity. Micro-labels must maintain medium-to-semibold weights to prevent anti-aliasing washout against obsidian dark surfaces.

## Layout & Spacing

The layout utilizes a structured feed-first column architecture anchored to an 8-point base grid:

- **Mobile (< 768px)**: Single column with `1rem` outer margins. Full-width feed items with `0.5rem` inline gutter separations between action clusters.
- **Tablet (768px - 1024px)**: 8-column layout with centered feed content maxing out at `640px` and adaptive navigation drawer.
- **Desktop (> 1024px)**: 12-column layout. Left persistent rail for channels and navigation (`260px`), central dynamic feed stream (`680px`), and right context panel (`320px`) for trending AI models, live signals, and category tags.

Consistent vertical rhythm is maintained with `space-md` (`1rem`) between post cards and `space-xs` to `space-sm` for interactive item groupings (e.g., reaction clusters, meta tags).

## Elevation & Depth

Visual hierarchy is established through translucent tonal layering and localized backdrops:

- **Level 0 (Canvas)**: Solid deep obsidian base `#090d16`.
- **Level 1 (Feed Surface)**: Translucent glass pane using `rgba(15, 23, 42, 0.75)` with `12px` backdrop blur (`backdrop-filter: blur(12px)`) and a `1px` subtle rim border (`rgba(255, 255, 255, 0.08)`).
- **Level 2 (Hover/Overlay)**: Elevated cards `rgba(30, 41, 59, 0.85)` with ambient cyan edge glow (`0 0 20px -5px rgba(6, 182, 212, 0.2)`).
- **Level 3 (Modal / Persistent Floating Bars)**: Frosted obsidian `rgba(15, 23, 42, 0.95)` with dual shadow: deep floor shadow (`0 20px 25px -5px rgba(0, 0, 0, 0.6)`) plus violet/cyan diffuse edge tinting.

## Shapes

The design system implements a balanced `roundedness: 2` geometry. Standard surfaces (post modules, interactive cards, entry panels) feature an 8px (`0.5rem`) corner radius. Larger modals and glass viewports use `rounded-lg` (16px / `1rem`), while notification callouts and detail drawers expand to `rounded-xl` (24px / `1.5rem`). 

Badges, chips, avatar rings, and counter pills use complete circular caps (`9999px`) to visually contrast against the structured, rectangular bounds of the content stream.

## Components

- **Feed Cards (Post Surfaces)**: Built with translucent glassmorphic surfaces (`1px` rim border). Post heads display the author's avatar, Sinhala/English handle, timestamp, and AI category identifier. Content supports mixed text rendering, code blocks, and embedded media frames.
- **Reaction Toolbar**: Horizontal array containing like, comment, share, and bookmark triggers. Default state is low-opacity slate (`#94a3b8`); active states trigger specific kinetic glows:
  - Like: Rose-red glow (`#f43f5e`)
  - Comment: Electric cyan glow (`#06b6d4`)
  - Share / Repost: Vivid blue glow (`#2563eb`)
  - AI Bookmark / Save: Violet glow (`#8b5cf6`)
- **Category Chips**: Compact pills featuring high-contrast text (`label-sm`). Default state is subtle slate background with translucent border. Selected or trending chips illuminate with cyan/violet borders and `10%` fill tint.
- **Buttons**:
  - *Primary*: Vibrant cyan solid fill with deep dark typography (`#090d16`), 8px border radius, semibold weight.
  - *Secondary / Glass*: Translucent slate background with border rim and white text.
  - *AI Action*: Gradient rim border (Cyan to Violet) with frosted interior.
- **Authentication & Input Layouts**: Clean, minimal form cards centered on blurred radial backdrops. Input fields feature deep inset slate fills, crisp 1px neutral borders, and bright cyan focus rings with zero outline bleed.