# .45 MAC-10 cyclic-rate reference

October 5, 2026. This is a game simulation target, not a claim that every physical MAC-family firearm has one exact cyclic rate.

The model study used the .45 ACP Ingram Model 10 reference in Kevin Dockery's armory. That specific primary collection data sheet lists a rate of fire of **950 cyclic rounds per minute**. This is the selected reference rate for the specific .45 variant, not a measurement of every specimen or ammunition combination. The original-manual mirror was inaccessible during this verification, so it is not relied on to substantiate the setting.

Accordingly, this game's .45 MAC-10 uses **950 RPM as its documented reference target**, rather than borrowing a figure from the 9mm Model 10 or .380 Model 11, or claiming an ammunition-independent universal rate. Other published numbers exist for other specimens/variants; this choice follows the source used for the art and the primary collection sheet.

Primary references:
- Kevin Dockery Armory, *Ingram Model 10 (.45 ACP)* collection sheet: `https://dockeryarmory.com/ingram-model-10-45-acp/`


Implementation: `Cyclic_Rate_RPM 950`, opt-in shared engine-free `ShotCadence`. At 50 Hz the exact period is 3000/950 = 60/19 ticks. Over 19 intervals, sixteen three-tick gaps plus three four-tick gaps total 60 ticks. The phase preserves that average during uninterrupted firing, reanchors after pauses, and never banks missed shots for a catch-up burst.

The rate setting concerns cyclic timing, not guaranteed average sustained fire including reloads, and not a real-world handling/instruction document. No physical weapon modification is proposed or modelled.
