---
name: TechPulse AI
colors:
  surface: '#faf8ff'
  surface-dim: '#d2d9f4'
  surface-bright: '#faf8ff'
  surface-container-lowest: '#ffffff'
  surface-container-low: '#f2f3ff'
  surface-container: '#eaedff'
  surface-container-high: '#e2e7ff'
  surface-container-highest: '#dae2fd'
  on-surface: '#131b2e'
  on-surface-variant: '#434655'
  inverse-surface: '#283044'
  inverse-on-surface: '#eef0ff'
  outline: '#737686'
  outline-variant: '#c3c6d7'
  surface-tint: '#0053db'
  primary: '#004ac6'
  on-primary: '#ffffff'
  primary-container: '#2563eb'
  on-primary-container: '#eeefff'
  inverse-primary: '#b4c5ff'
  secondary: '#006780'
  on-secondary: '#ffffff'
  secondary-container: '#76dcff'
  on-secondary-container: '#006077'
  tertiary: '#005a89'
  on-tertiary: '#ffffff'
  tertiary-container: '#0073ae'
  on-tertiary-container: '#e7f2ff'
  error: '#ba1a1a'
  on-error: '#ffffff'
  error-container: '#ffdad6'
  on-error-container: '#93000a'
  primary-fixed: '#dbe1ff'
  primary-fixed-dim: '#b4c5ff'
  on-primary-fixed: '#00174b'
  on-primary-fixed-variant: '#003ea8'
  secondary-fixed: '#b7eaff'
  secondary-fixed-dim: '#6cd3f7'
  on-secondary-fixed: '#001f28'
  on-secondary-fixed-variant: '#004e61'
  tertiary-fixed: '#cce5ff'
  tertiary-fixed-dim: '#93ccff'
  on-tertiary-fixed: '#001d31'
  on-tertiary-fixed-variant: '#004b73'
  background: '#faf8ff'
  on-background: '#131b2e'
  surface-variant: '#dae2fd'
typography:
  display:
    fontFamily: Plus Jakarta Sans
    fontSize: 3rem
    fontWeight: '800'
    lineHeight: '1.15'
    letterSpacing: -0.03em
  headline-lg:
    fontFamily: Plus Jakarta Sans
    fontSize: 2.25rem
    fontWeight: '700'
    lineHeight: '1.25'
    letterSpacing: -0.02em
  headline-lg-mobile:
    fontFamily: Plus Jakarta Sans
    fontSize: 1.75rem
    fontWeight: '700'
    lineHeight: '1.3'
    letterSpacing: -0.015em
  headline-md:
    fontFamily: Plus Jakarta Sans
    fontSize: 1.5rem
    fontWeight: '700'
    lineHeight: '1.35'
    letterSpacing: -0.01em
  headline-sm:
    fontFamily: Plus Jakarta Sans
    fontSize: 1.125rem
    fontWeight: '600'
    lineHeight: '1.4'
    letterSpacing: -0.005em
  body-lg:
    fontFamily: Manrope
    fontSize: 1.125rem
    fontWeight: '400'
    lineHeight: '1.7'
    letterSpacing: 0em
  body-md:
    fontFamily: Manrope
    fontSize: 0.9375rem
    fontWeight: '400'
    lineHeight: '1.65'
    letterSpacing: 0em
  body-sm:
    fontFamily: Manrope
    fontSize: 0.8125rem
    fontWeight: '400'
    lineHeight: '1.55'
    letterSpacing: 0.005em
  label-md:
    fontFamily: Plus Jakarta Sans
    fontSize: 0.875rem
    fontWeight: '600'
    lineHeight: '1.4'
    letterSpacing: 0.01em
  label-sm:
    fontFamily: Plus Jakarta Sans
    fontSize: 0.75rem
    fontWeight: '700'
    lineHeight: '1.3'
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
  gutter-md: 1.5rem
  margin: 1rem
  margin-md: 1.5rem
  margin-lg: 2rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 1rem
  space-lg: 1.5rem
  space-xl: 2rem
---

## Brand & Style

This design system delivers a high-clarity, authoritative, and fast-paced editorial experience tailored for bilingual technical journalism and AI news feeds. Drawing from Modern Corporate and Editorial Precision paradigms, the interface balances high-density information architecture with effortless readability. 

The aesthetic is characterized by clean, luminous canvas tiers, high-contrast slate typography, and electric sapphire-cyan focal points. Rather than decorative excess, the interface relies on deliberate typographic scale, structured cards, and ambient, diffused elevation to establish visual hierarchy. The emotional tone conveys technical precision, speed, and trusted intelligence—empowering users to scan, dissect, and absorb complex technical discourse across Sinhala and Latin scripts with minimal cognitive fatigue.

## Colors

The color architecture is built for maximum legibility in daylight environments, combining a vibrant sapphire primary anchor with energetic cyan accents over clean slate backdrops.

- **Primary (`#2563eb`):** Anchors high-priority interactive components, primary button fills, active navigation tabs, and critical badge indicators.
- **Secondary (`#0891b2`):** Serves as an analytical accent for AI insights, trend metrics, interactive code tokens, and subtle background glow highlights.
- **Tertiary (`#0284c7`):** Applied to hyperlinks, secondary data visualizations, and contextual actions.
- **Neutral Surface Palette:**
  - Base Canvas: `#f8fafc` (Slate 50) for outer viewport containment.
  - Surface Card: `#ffffff` for elevated feed items, detail dialogs, and navigation headers.
  - Sub-surface / Muted Tiers: `#f1f5f9` (Slate 100) for nested metadata pills, search bars, and subtle component dividers.
- **Text & Contrast Hierarchy:**
  - Primary Typography: `#0f172a` (Slate 900) to ensure strict WCAG AAA conformance against white surfaces.
  - Secondary / Supporting Typography: `#334155` (Slate 700) for body copy, author lines, and summaries.
  - Muted Metadata / Timestamps: `#64748b` (Slate 500).
  - Outlines & Ghost Borders: `#e2e8f0` (Slate 200).

## Typography

Typography plays a critical architectural role due to bilingual script demands. The structural pairing uses Plus Jakarta Sans for geometric, tech-forward headlines and metadata labels, alongside Manrope for body text. 

For Sinhala script coverage, fallbacks must cascade to `Noto Sans Sinhala` across all text tiers. Because Sinhala glyphs have complex conjuncts and taller vertical ascender/descender bounds:
- **Line Heights:** Body line-heights are intentionally relaxed (1.65–1.7) to prevent script collision and maintain rhythmic vertical scanning.
- **Typographic Fallbacks:** `font-family: 'Manrope', 'Noto Sans Sinhala', sans-serif;` for body blocks, and `'Plus Jakarta Sans', 'Noto Sans Sinhala', sans-serif;` for display and headline levels.
- **Hierarchy Rules:** Display weights are reserved for featured AI editorial headlines. Standard feed card titles consistently implement `headline-sm` to maintain high screen density on both mobile viewports and desktop grid layouts.

## Layout & Spacing

The layout is anchored on a structured 12-column fluid grid system across desktop screens, reflowing down to 1 column on mobile devices.

- **Breakpoints:**
  - Mobile: `< 640px` (Single feed stream, 1rem screen margins, full-bleed card option).
  - Tablet: `640px – 1024px` (Main stream + contextual side-rail, 1.5rem gutters).
  - Desktop: `> 1024px` (Centered 3-column architecture: navigation anchor, primary 680px editorial feed container, and right-rail analytical trends container).
- **Rhythm & Padding:** All interior component padding follows multiples of the 4px baseline. Feed cards utilize `space-md` (16px) internal padding on mobile and `space-lg` (24px) on desktop to balance reading comfort with information density.

## Elevation & Depth

Visual hierarchy uses a refined ambient elevation system paired with subtle surface boundaries. Instead of heavy drop shadows, the interface creates depth using soft, diffused multi-stop shadows with cool slate undertones.

- **Level 0 (Flat / Canvas):** Applied to the viewport foundation (`#f8fafc`). No elevation.
- **Level 1 (Feed Cards & Surface Elements):** Clean `#ffffff` fill surrounded by a structural ghost border (`1px solid #e2e8f0`) and an ambient shadow: `0 1px 3px 0 rgba(15, 23, 42, 0.04), 0 1px 2px -1px rgba(15, 23, 42, 0.03)`.
- **Level 2 (Hovered Feed Cards & Dropdowns):** `0 4px 12px -2px rgba(15, 23, 42, 0.06), 0 2px 6px -2px rgba(15, 23, 42, 0.04)`. Card borders shift slightly to `#cbd5e1`.
- **Level 3 (Sticky Headers & Modals):** `0 10px 25px -5px rgba(15, 23, 42, 0.08), 0 8px 10px -6px rgba(15, 23, 42, 0.04)`. Sticky navigation bars incorporate a backdrop blur (`backdrop-filter: blur(12px)`) with an 85% opacity surface fill (`rgba(255, 255, 255, 0.85)`).

## Shapes

The design system implements a roundedness value of `2`. This creates a balanced, modern, and friendly consumer-tech aesthetic that avoids both severe clinical edges and over-rounded organic forms.

- **Default Border Radius (`0.5rem` / 8px):** Standard buttons, text input fields, drop-down menus, tag chips, and preview thumbnails.
- **Large Radius (`rounded-lg` / `1rem` / 16px):** Core feed cards, modal containers, and floating promotional banners.
- **Extra Large Radius (`rounded-xl` / `1.5rem` / 24px):** Standalone hero promo blocks and interactive AI prompt drawers.
- **Full Radius (`9999px`):** Status indicators, pill filters, avatars, and floating reaction quick-buttons.

## Components

### Feed Cards
- **Architecture:** Facebook-inspired clean feed cards built on `#ffffff` with a `1px solid #e2e8f0` border and `rounded-lg` radius.
- **Structure:** Author/Source bar at top (avatar + source name + Sinhala/English time ago + category badge), followed by bold `headline-sm` post title, clamped 3-line `body-md` synopsis, full-bleed or inset 16:9 media container, and a bottom social/share action bar.
- **Interactive State:** Transitions border color to `#cbd5e1` with Level 2 elevation on hover.

### Buttons
- **Primary:** Solid `#2563eb` fill with `#ffffff` text, 8px radius, `label-md` typography. Hover shifts to `#1d4ed8`.
- **Secondary:** Subtle `#f1f5f9` fill with `#0f172a` text. Hover shifts to `#e2e8f0`.
- **Ghost:** Transparent fill with `#334155` text; hover reveals `#f1f5f9`.
- **Padding:** 0.625rem vertical by 1.25rem horizontal.

### Chips & Category Badges
- **Editorial Badges:** Small `label-sm` pills with low-opacity tinted backgrounds. AI news uses `#0891b2` at 10% opacity with `#0e7490` text. General tech uses `#2563eb` at 10% opacity with `#1d4ed8` text.
- **Filter Chips:** 8px radius, 1px `#e2e8f0` border, `#ffffff` surface. Selected state uses `#0f172a` fill with `#ffffff` text.

### Input Fields & Search Bars
- **Style:** Background `#f1f5f9`, border `1px solid transparent`, text `#0f172a`, placeholder text `#64748b`.
- **Focus State:** Background transitions to `#ffffff`, border switches to `2px solid #2563eb`, accompanied by an ambient sapphire glow (`box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.15)`).

### Checkboxes & Radio Buttons
- **Style:** 8px radius for checkboxes (2px internal corner radius for inner tick box), full circle for radio.
- **Active State:** `#2563eb` fill with white indicator icon.
- **Border:** `1.5px solid #cbd5e1` in neutral resting state.

### Lists & Navigation
- **Feed Lists:** Separated by `12px` vertical margins over the canvas, avoiding visible full-width rule dividers to reduce clutter.
- **Sidebar Nav:** Compact vertical items with 8px radius; active links display a left border indicator (`3px solid #2563eb`) with an active tint fill of `rgba(37, 99, 235, 0.08)`.