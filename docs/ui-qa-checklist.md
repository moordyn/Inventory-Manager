# Inventory Manager — UI and camera acceptance checklist

These tests are required before calling a deployment production-ready. Automated CI catches syntax, CSS balance and missing DOM references, but cannot prove camera performance or mobile layout.

## Sign-in and navigation
- [ ] On iPhone Safari, Android Chrome, and desktop Chrome/Edge, employee ID sign-in opens Home without flashing back to login.
- [ ] Global admin PIN sign-in opens Home; the Administration tab appears only for authorized users.
- [ ] Mobile bottom navigation contains Home, Items, In, Out, History and role-based Admin without clipping at 320, 375, 390 and 430 CSS pixels.
- [ ] The mobile account header has a working Settings and Sign out button.
- [ ] Inactivity countdown reaches sign out only after 30 seconds without interaction; valid activity resets it.
- [ ] Changing store updates the location heading, products and permissions.

## Responsive display
- [ ] Test 320, 375, 390, 430, 760, 1024 and 1440 px viewports.
- [ ] All four overview metric cards fit without horizontal page scroll.
- [ ] Recent inventory, recent activity, all inventory, transaction history and store administration lists do not overflow.
- [ ] Long SKUs, product names, employee names and notes wrap without covering actions.
- [ ] Full-width buttons and inputs remain tappable when iPhone keyboard is open.
- [ ] Modal forms, product deletion confirmation and edit store location are usable in portrait and landscape.
- [ ] Keyboard Tab/Shift+Tab stays in a modal and Escape dismisses it; focus returns to the trigger.
- [ ] Search and stock filters update the inventory count and do not alter stock.

## Camera barcode reader
- [ ] Grant camera permission on iPhone Safari; rear camera opens once without switching streams for ZXing fallback.
- [ ] Scan a UPC-A and EAN-13 retail barcode; result opens the correct product on first recognition.
- [ ] Scan Code 128 and Code 39 inventory labels and a QR code where applicable.
- [ ] In good lighting, test barcode recognition at multiple distances; try a reflective package.
- [ ] Where supported, Turn on flashlight / Turn off flashlight works without restarting the camera.
- [ ] On a browser supporting native BarcodeDetector, detection starts promptly and stops at first result.
- [ ] On Safari or a browser without BarcodeDetector, ZXing starts without a second camera permission request.
- [ ] Manual entry closes camera tracks and places focus in the barcode search field.
- [ ] Closing the dialog, navigating away, switching browser tabs, or signing out releases the camera.
- [ ] Poor network or blocked CDN displays a useful compatibility decoder error and manual entry remains available.
- [ ] Retry scanner after success, cancellation, camera-denied error, or reopening the dialog.

## Administration / email
- [ ] Store admins see recipient settings but cannot see the global Gmail/OAuth configuration.
- [ ] Global admin can connect the one sending Gmail account and send a test notification for each store.
- [ ] An invalid or oversized OAuth client JSON is rejected without exposing a secret in the interface.
- [ ] A valid Google OAuth import works; returning from Google does not trigger a spurious logout.
- [ ] Long recipient addresses wrap, and all admin forms fit within the mobile viewport.

## Release readiness beyond visual checks
- [ ] CI .NET Release build passes.
- [ ] Validate schema migrations against a copy of production PostgreSQL.
- [ ] Take and restore a backup to confirm the database and encryption key can be recovered.
- [ ] Confirm private environment variables are not included in Git or sent to browsers.
- [ ] Fix or mitigate employee-ID-only authorization before treating store-admin actions as securely authenticated.
- [ ] Verify Gmail authorization and automatic alert delivery in the intended Google OAuth publishing mode.
- [ ] Check accessibility with keyboard and screen reader, and test on real iOS Safari hardware.
- [ ] Monitor logs and browser console for 4xx/5xx failures during a smoke-test session.
