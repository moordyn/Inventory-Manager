# Themes and branding (next release)

Inventory Manager provides **ten themes (nine dark, one light)**, each with its own coordinated background, surface, accent, contrast and ZAGG wordmark/favicon colors. These settings affect **appearance only**—not identity, permissions, inventory data, business settings or email.

## The ten themes

| Theme | Theme ID | Background | Panels | Primary accent | Design direction |
| --- | --- | --- | --- | --- | --- |
| Evergreen | `mint` | `#0d141d` | `#192430` | `#91ddba` | Original charcoal and mint style |
| Deep Purple | `purple` | `#100d1d` | `#211936` | `#c6a0ff` | Dark amethyst and violet |
| Midnight Ocean | `ocean` | `#091923` | `#112c3c` | `#67d9ed` | Navy, teal and cyan |
| Copper Ember | `ember` | `#1b1310` | `#31221a` | `#f2ad78` | Espresso, copper and amber |
| Rose Noir | `rose` | `#1a1019` | `#321f30` | `#f4a4cf` | Deep plum and muted blush |
| Arctic Slate | `slate` | `#111820` | `#202c39` | `#9ec4ec` | Modern graphite and frost blue |
| Forest Night | `forest` | `#0b1712` | `#193126` | `#a1dc82` | Deep pine and soft moss green |
| Champagne Gold | `gold` | `#181510` | `#30281e` | `#e9c878` | Dark onyx and warm champagne |
| Crimson Night | `crimson` | `#190e12` | `#341c23` | `#f3899b` | Oxblood and ruby |
| Glacier Light | `glacier` | `#f3f7fc` | `#ffffff` | `#176e99` | **Light mode:** icy white and cobalt blue |

All ten use the same information architecture and responsive page layout. The selector shows three sample color swatches and highlights the selected palette. The logo geometry and text stay consistent across brands while the accent and supporting lettering are recolored; the matching favicon changes too.

## Interactive login fluid simulation

The sign-in, first-time setup, and password recovery screen now uses **mouse-driven, theme-coordinated fluid smoke** rather than independent expanding radial-glow particles. A self-contained **WebGL2 fluid solver** advects colored dye through a velocity field, computes curl/vorticity, divergence, an iterative pressure projection, and subtracts the pressure gradient. Mouse dragging adds momentum and pigment to the fluid; nearby color is displaced into swirling clouds, eddies and dissipating plumes. Gentle autonomous motion keeps the background alive without input.

The target visual reference is the **Windhawk Fluid Wallpaper 1.0.0** (October 2026), which is a Direct3D port of Pavel Dobryakov's MIT-licensed WebGL Fluid Simulation. The login implementation remains self-contained and uses the corresponding GPU fluid principles; it is theme-aware rather than copying the source's fixed/default color scheme. References: https://www.reddit.com/r/Windhawk/comments/1x184qq/fluid_wallpaper_100/ and https://github.com/PavelDoGreat/WebGL-Fluid-Simulation .

The renderer's display shader was repaired after an invalid GLSL block prevented successful compilation in real browsers. The dedicated Chromium GPU smoke test in `tests/login-fluid-browser.cjs` checks that mouse movement produces actual rendered pixels, not merely that the JavaScript parses. The visual result is still subject to comparison with the reference, especially bloom and sunray intensity.

The revised fluid effect responds to **ordinary mouse movement and stronger click-drag gestures**, without continuously emitting dye. A replenishing per-second ink budget limits how much color can accumulate even during long movements. A drag adds a small, bounded amount of colored ink and momentum; ordinary mouse hovering does not paint. The fluid disperses and fades instead of building up until the entire screen is covered. Dye is rendered at a higher resolution with smooth sampling, while the cheaper velocity/pressure grid remains lower resolution. The canvas is transparent over a theme-matched CSS gradient to prevent a black or flat background.

The whole canvas renders against the currently configured **global login theme** (the page is *never forced to black*). Theme colors are drawn from the existing accent, active-border, muted and background variables. **Glacier Light** uses different blending to preserve a pale base, while dark palettes have vivid, saturated fluid clouds.

The canvas remains behind the form and is `pointer-events:none`; the containing login screen listens passively to pointer movement so users can still focus and submit the form. The effect runs only on login pages and never replaces the separate **non-interactive** signed-in dashboard animation. It stops after sign-in, on tab hiding and for **Reduce Motion**. The fallback on devices without usable WebGL2/float render targets is the static theme-based CSS gradient rather than a blank or black screen.

Performance limits: the simulation renders at around 30 FPS, caps display DPR at 1.25, uses low-resolution velocity/pressure buffers and moderate pressure iterations. No external script service, image downloads, or new environment variables are required.

Implementation: [`server/Inventory.Api/wwwroot/assets/login-fluid.js`](../server/Inventory.Api/wwwroot/assets/login-fluid.js); markup and theme binding remain in [`index.html`](../server/Inventory.Api/wwwroot/index.html).

**Visual QA remains necessary:** compare dragging trajectories, pressure displacement, color density, and cloud diffusion against the chosen Wallpaper Engine references in real desktop browsers. Verify all ten palettes, Glacier Light, touch and mouse behavior, first-load appearance, form usability, reduced motion and GPU fallback.

## Animated signed-in background

The main inventory application has its own **non-interactive, theme-matched ambient background**. Its larger, brighter overlapping color fields are deliberately visible around and subtly through the gently translucent cards/sidebar; panel blur keeps labels and controls readable. Four oversized diffused light fields drift slowly behind the UI. Their colors come from each employee's selected theme (accent, active-border and muted colors), independent of the global login theme. Glacier Light renders them at lower intensity for white and pale surfaces.

This canvas is fixed to the viewport, so scrolling through inventory and transaction history does not create a giant graphics buffer. It renders at no more than **20 frames per second**, caps device pixel ratio to **1.25**, and stays behind the sidebar, forms, cards, tables, and navigation. The background does not handle pointer input; only the separate sign-in aurora responds to user movement.

The dashboard canvas starts when the user enters the signed-in UI, refreshes its colors immediately upon personal theme changes, and stops on logout, tab hiding or a system Reduce Motion preference. Reduce Motion produces a static theme-colored background instead of animation. No additional library or server feature flag is needed.

## Personal dashboard theme

Every signed-in employee—including ordinary employees and all admins—can open **Settings → Appearance** and select any of the ten themes. The selection is stored in the server database as `employees.theme`, not merely browser local storage, so it remains available after logout and on other devices. The API validates the theme against the ten allowed IDs, and an authenticated employee may update **only their own** preference:

- `GET /api/auth/me` includes `theme`.
- `PUT /api/auth/theme` accepts JSON `{"theme":"purple"}`.
- An unrecognized theme gets HTTP 400; anonymous requests are rejected.

The default preference for existing and new employee records is **Evergreen (`mint`)**. It is independent of the public login theme.

## Global login-screen theme

Only a **global administrator** can use **Administration → Login screen appearance** to choose the public login and first-time enrollment palette. Store admins and ordinary employees cannot see or change this setting. The choice is shared by the whole installation:

- `GET /api/auth/status` anonymously returns `loginTheme` along with authentication initialization status. Theme names are public presentation configuration, not secrets.
- `PUT /api/admin/theme/login` accepts JSON `{"theme":"ocean"}`, **requires a global-admin session**, validates input and writes `settings.login_theme`.
- Changes are recorded in `admin_audit` as `theme.login_changed`; they affect subsequent login views and new page loads.

When a person signs in, the interface uses their **personal theme**. On sign-out, the interface returns to the configured **public login theme**. A global admin changing the login theme does not overwrite anyone's personal preference.

## Implementation reference

- API, authorization, migration: [`server/Inventory.Api/Program.cs`](../server/Inventory.Api/Program.cs).
- CSS palettes, theme choices, asset switching and initial load: [`server/Inventory.Api/wwwroot/index.html`](../server/Inventory.Api/wwwroot/index.html).
- Matching branding: `server/Inventory.Api/wwwroot/assets/zagg-wordmark-{theme}.svg` and `zagg-favicon-{theme}.svg`; original non-suffixed SVGs are still provided for compatibility.
- Integration tests: [`tests/api-smoke.mjs`](../tests/api-smoke.mjs).
- CI checks: [`.github/workflows/build.yml`](../.github/workflows/build.yml).

### Light-mode details

**Glacier Light** uses a light browser `color-scheme` and a dark-colored wordmark on pale backgrounds. Additional light-mode overrides apply to cards, menu navigation, mobile rows, forms, table text, tags, status messages, alerts, modal surfaces and keyboard focus. The corresponding light favicon has a pale background and dark lettering. Test browser input autofill, camera scanner overlays and accessibility contrast on real devices.

No external theme service, API keys or new environment variables are required; the existing PostgreSQL persistence and authorization infrastructure is reused.

## QA and rollout

1. Open login from a fresh anonymous browser; check the global palette and matching wordmark/favicon.
2. Sign in with two different employees; set different personal themes, refresh, log out, and sign back in on another device/browser. Preferences should persist independently.
3. As a store admin, attempt the admin login-theme endpoint: HTTP 403; verify the admin appearance panel is not shown.
4. As a global admin, change the login theme, then verify it on a new anonymous login view. Check that existing personal themes are unchanged and a `theme.login_changed` audit event is present.
5. Test responsive desktop and iPhone sizes; camera overlays, input focus states, alert text and disabled buttons must remain legible in all themes.
6. Inspect all ten SVG variants and favicon updates. Do not distort the wordmark or replace its geometry.
7. Before production use, confirm CI passes, run the manual UI checks and back up the database/`.env` as described in [Operations](operations.md).

**Release status:** These features are changes on `main` intended for the next release; they are **not part of the immutable v1.2.0 tag** until a new version is tagged and released.
