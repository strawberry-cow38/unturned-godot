//! Original immutable display names from the managed source extraction.
#[derive(Clone, Debug, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Definition {
    pub glass_names: Vec<String>,
}
