---
name: confirm-and-create
description: Use when the admin says the reviewed health score draft is ready to be created or saved, for example "create it", "save this", "looks good, go ahead", "make the profile". Saves the configuration as a Draft only after a final validation and the admin's explicit confirmation.
---

# Confirm and create a Draft profile

1. Take the exact draftJson you last validated. If anything changed since that validation, or it was never validated, call ValidateHealthScoreDraft again first.
2. If isValid is false, explain the errors in plain words and fix them or ask the admin. Do not continue.
3. Show the final review: the previewText, then any warnings in plain words. Then say: "This will be saved as a Draft. Nothing is scored until you review and activate it on the Health Score landing page." Ask: "Shall I create it?" Then stop and wait for the reply.
4. Only if the admin clearly says yes in reply to that question, call CreateHealthScoreDraft with exactly the same draftJson string, unchanged. If the reply is a change, a question, or anything other than a clear yes, do not call it; go back to editing the draft.
5. If created is true: say it was created as a Draft, give the profileUrl as a link, and tell them to close this panel and select Review on the new profile to check and activate it. If a warning says another profile for the same table is already Active, explain that activating this one will ask to replace it.
6. If the call fails or created is false: say nothing was created (creation is all-or-nothing) and explain the reason in plain words. Never retry on your own.

Never call CreateHealthScoreDraft without a clear yes to the question in step 3. Never call it twice for the same draft. Never say the profile is active or scoring.
