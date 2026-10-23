// Health Score Rule form script.
//
// Pre-selects the data source's table in the Condition (FetchXML builder) control, so admins never pick
// the table twice. Register prefillCondition on the form's OnLoad and on the Data Source lookup's OnChange,
// with "Pass execution context as first parameter" ticked.
//
// The condition builder is a third-party control, so its table dropdown can only be pre-selected, not locked.
// The scoring engine and the readiness check both reject a rule whose table differs from its data source.

/* exported prefillCondition */
async function prefillCondition(executionContext) {
  const form = executionContext.getFormContext();
  const condition = form.getAttribute("reliance_condition");
  const dataSource = form.getAttribute("reliance_datasourcelookup")?.getValue();

  // Only new or empty rules that already have a data source
  if (!condition || condition.getValue() || !dataSource) return;

  try {
    const ds = await Xrm.WebApi.retrieveRecord(
      "reliance_healthscoredatasource",
      dataSource[0].id.replace(/[{}]/g, ""),
      "?$select=reliance_datasourcename"
    );

    // The table picker stores "opportunity" or "Opportunity (opportunity)"
    const raw = ds.reliance_datasourcename || "";
    const match = /\(([^()]+)\)\s*$/.exec(raw);
    const table = (match ? match[1] : raw).trim().toLowerCase();

    if (table) {
      condition.setValue(`<fetch><entity name="${table}"><all-attributes/></entity></fetch>`);
    }
  } catch (e) {
    console.warn("Health Score: could not pre-fill the rule condition.", e);
  }
}
