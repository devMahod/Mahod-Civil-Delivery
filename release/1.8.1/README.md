# Mahod Civil Delivery 1.8.1 — local test package

This updates the standalone Delivery 1.8.0 package previously prepared for the owner, for local testing of the merged `dev-itay` interface on `DEV-HAIM`. It is not a MahodAI/Mahod Tools feed release. The integrated office release workflow remains documented in the repository's `AGENTS.md`; no main branch, production feed or portal is published by this package build.

The guide now describes the light interface, the Project landing tab and general BOQ launcher, the recommended-item badge, one-click automatic assignment, undo and quantity-unit behaviour. The product retains the merged design and BOQ implementation. Packaging carries the four exact Rubik font weights and their OFL licence beside each host DLL, and verifies the newly embedded green Mahod mark. Previous local installers remain available separately.

## Verification

Frozen-source identity, guide identity and installer checks will be recorded after the build. The new staging tests exercise the actual font-copy functions, including missing or altered files, unexpected content and rejecting invalid input before copying. The payload must include only the six runtime files and five pinned font/licence files per host, plus the guide, uninstaller and package declaration: 25 files total.

The preceding design merge passed 162 checks; all 11 UI failures also occurred on one of its parents. Those results are recorded locally in `MahodCivilDelivery/out/dev-itay-merge-validation/validation.json` and are not a fully green product release gate or native acceptance of this package. Private catalogues, project archives and research output remain outside the package.
