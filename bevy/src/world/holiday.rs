//! Original WorldBuilder/ResourceField holiday gates. Prop keys retain unfiltered
//! source rows. Resource keys retain (name, instance); their active concatenation
//! index comes from the accepted ServiceManifest, never a native filtered counter.
use std::{collections::HashMap, path::Path};

pub(super) fn restrictions(content: &Path) -> HashMap<String, String> {
    std::fs::read_to_string(content.join("objects/holiday_props.txt"))
        .unwrap_or_default()
        .lines()
        .filter_map(|line| {
            let mut fields = line.split_whitespace();
            Some((
                fields.next()?.to_ascii_lowercase(),
                fields.next()?.to_owned(),
            ))
        })
        .collect()
}
pub(super) fn prop_allowed(
    restrictions: &HashMap<String, String>,
    guid: &str,
    active: &str,
) -> bool {
    restrictions
        .get(guid)
        .is_none_or(|required| required == active)
}
pub(super) fn resource_allowed(required: &str, active: &str) -> bool {
    required == "NONE" || required == active
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_manifest_filters_holiday_content_without_renumbering_rows() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        let restrictions = restrictions(&root);
        let names = crate::content::load_guid_map(&root).unwrap();
        let source = std::fs::read_to_string(root.join("objects/placements.txt")).unwrap();
        let placements = super::super::parse_placements(&source, &names).unwrap();
        let normal: Vec<_> = placements
            .iter()
            .filter(|p| prop_allowed(&restrictions, &p.guid, "NONE"))
            .collect();
        let christmas: Vec<_> = placements
            .iter()
            .filter(|p| prop_allowed(&restrictions, &p.guid, "CHRISTMAS"))
            .collect();
        assert!(christmas.len() > normal.len());
        for row in &normal {
            assert!(
                christmas
                    .iter()
                    .any(|other| other.source_index == row.source_index && other.guid == row.guid)
            );
            assert!(
                source
                    .lines()
                    .nth(row.source_index)
                    .unwrap()
                    .starts_with(&row.guid)
            );
        }
        let resources = std::fs::read_to_string(root.join("resources/resources.txt")).unwrap();
        assert!(
            resources
                .lines()
                .any(|line| line.split_whitespace().nth(2) == Some("CHRISTMAS"))
        );
        assert!(resource_allowed("NONE", "CHRISTMAS"));
        assert!(resource_allowed("CHRISTMAS", "CHRISTMAS"));
        assert!(!resource_allowed("CHRISTMAS", "NONE"));
        assert!(!resource_allowed("HALLOWEEN", "CHRISTMAS"));
    }
}
