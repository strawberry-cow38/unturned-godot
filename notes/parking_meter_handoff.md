# Parking meter — simplified revision

Strawberry feedback1559859409117511702 (October14,2026): remove dial/slots, keep shape, square post, no post base. This replaces the first-review art in cefd4866 (cefd4866553bbe4d6fba02d199dad28e09670481). Approval/registration still pending.

## Current asset
Parking_Meter_0 keeps proposedGUIDd58deacfeb1f4e82849a07411a160c49. **24 geometric vertices /40 triangles**, one flat-shaded mesh/one opaque2x2nearest palette. **.52mW x .28mD x2.10mH**.

- Exact original eight-corner head outline and .28m head depth retained.
- Face is completely blank: dial, marks, needle, coin-slot plate/recess and turning hardware removed.
- Post is a plain **.14x.14m square**, from groundZ=0 to1.48m; it enters the head8cm.
- Foot/base removed completely.
- Metal greys176RGB fromChair_Metal_0 and130RGB fromStreet_Light_0 retained. Same2x2palette file; old pale/black lower-row texels now unused.
- Native metres,+Zup,+Yfront,postXYcentre/baseZ0. StandardGodotconversionYup/front-Z. **Already upright: do NOT apply retailhedge/bench270degX correction.**

Overall depth decreased.365->.28m because the front hardware is gone; the actual housing dimensions/silhouette are unchanged. No new GUID, no renamedprop. ReferencesMailbox_0/Chair_Metal_0 are real native-scale stock art, excluded from mainOBJ/GLB and registration.

## Integration
Art-only revision: no catalog/registry, town placement, core/protocol or payment/timer code. Cow/Tiny own registration/placement AFTERapproval. Same requested kerbside/parking-pedestrian-edge metadata, avoiddriveways/doorways/aisles/throughroutes. Frontaccess.6m is an art placement suggestion, not a legal/accessibility rule. Do not use the superseded184triangleface-detailed first-review OBJ.

## Review/source
Three actualGodotcapturesfront/angle with native1xstockreferences +headclose; allfinalviewsvisuallyinspected throughMCP. Clean source restored afterward, noQAimagecards. No test suites run.

Editable Parking_Meter_Simple_Revision.blend has3meshes:meter+2REFERENCE__stockprops,3packedpalettes. Recipe/config/OBJ/GLB/nativeJSON/manifest and reproducibleGodotreview supplied. Recipe replacescurrentBlender scene: use separateemptyartscene.

BranchStaging-Astra-Parking-Meter-Simple is basedcefd4866553bbe4d6fba02d199dad28e09670481; latestdelta ONLYMeterOBJ+2notes/manifestfiles. Bundleincludesfirstreviewcommittoo, requirespublished862306c7131ab2a71efd7b8cc45cf4f8fed5249f. No remote push or approval claimed.
