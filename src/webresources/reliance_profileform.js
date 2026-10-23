/* Health Score Profile form script.
   Web resource: reliance_ProfileForm.js
   - Form OnLoad:   HealthScoreProfileForm.onLoad   (pass execution context)
   - Done command:  HealthScoreProfileForm.done     (parameter: PrimaryControl)
*/

var HealthScoreProfileForm = (function () {
    "use strict";

    var LANDING_PAGE = "reliance_AIHealthScoreHome.html";
    var CHECKLIST_PAGE = "reliance_ProfileChecklist.html";
    var STATUS_ACTIVE = 2;
    var NOTE_ID = "hs-profile-guidance";

    function onLoad(executionContext) {
        var formContext = executionContext.getFormContext();

        applyState(formContext);

        // After a save (including the first save of a new profile), update the form and the checklist.
        formContext.data.entity.addOnPostSave(function () {
            applyState(formContext);
            refreshChecklist(formContext);
        });

        // When a subgrid changes (e.g. a data source is added), update the checklist.
        formContext.ui.controls.forEach(function (control) {
            if (typeof control.getControlType === "function" &&
                control.getControlType() === "subgrid" &&
                typeof control.addOnLoad === "function") {
                control.addOnLoad(function () {
                    refreshChecklist(formContext);
                });
            }
        });
    }

    function applyState(formContext) {
        var statusAttr = formContext.getAttribute("reliance_status");
        var status = statusAttr ? statusAttr.getValue() : null;

        // Status only changes through the Review and activate dialog.
        var statusControl = formContext.getControl("reliance_status");

        if (statusControl) {
            statusControl.setDisabled(true);
        }

        formContext.ui.clearFormNotification(NOTE_ID);

        // Guidance for new and Draft profiles now lives in the progress checklist.
        // The banner is only used for Active profiles, whose fields are locked.
        if (status === STATUS_ACTIVE) {

            formContext.ui.controls.forEach(function (control) {
                if (typeof control.setDisabled === "function") {
                    control.setDisabled(true);
                }
            });

            formContext.ui.setFormNotification(
                "This profile is active and locked. To make changes, deactivate it from the Health Score landing page.",
                "WARNING",
                NOTE_ID
            );
        }
    }

    // Asks the checklist web resource on this form to reload.
    // Finds it by file name, so the control's name on the form doesn't matter.
    function refreshChecklist(formContext) {
        var id = (formContext.data.entity.getId() || "").replace(/[{}]/g, "");

        formContext.ui.controls.forEach(function (control) {
            if (typeof control.getControlType !== "function" ||
                control.getControlType() !== "webresource" ||
                typeof control.getSrc !== "function" ||
                typeof control.getContentWindow !== "function") {
                return;
            }

            var src = (control.getSrc() || "").toLowerCase();
            if (src.indexOf(CHECKLIST_PAGE.toLowerCase()) === -1) return;

            control.getContentWindow().then(function (win) {
                if (win && typeof win.refreshChecklist === "function") {
                    win.refreshChecklist(id);
                }
            }).catch(function () {
                // The checklist isn't loaded yet; it will load itself.
            });
        });
    }

    async function done(formContext) {
        if (!formContext) return;

        // Save first if the profile is new or has unsaved changes.
        if (
            formContext.ui.getFormType() === 1 ||
            formContext.data.entity.getIsDirty()
        ) {
            try {
                await formContext.data.save();
            } catch (e) {
                return; // the form already shows why the save failed
            }
        }

        var id =
            (formContext.data.entity.getId() || "")
                .replace(/[{}]/g, "");

        if (!id) return;

        await Xrm.Navigation.navigateTo(
            {
                pageType: "webresource",
                webresourceName: LANDING_PAGE,
                data: "profileId=" + id
            },
            {
                target: 1
            }
        );
    }

    return {
        onLoad: onLoad,
        done: done
    };
})();
