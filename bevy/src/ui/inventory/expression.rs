//! Scalar Expression/SpinBox semantics from Godot 4.6. See
//! licenses/GODOT_EXPRESSION.txt. This evaluator has no scene or object access.
mod functions;
mod interpolation;
mod lexer;
mod parser;

#[derive(Clone, Debug, PartialEq)]
enum Value {
    Int(i64),
    Float(f64),
    Bool(bool),
    Text(String),
    Array(Vec<Value>),
    Nil,
    // Execution can return a non-Nil error payload at the root. A parent
    // operator discards that payload and returns Nil, as original Expression.
    Fault(Box<Value>),
}
impl Value {
    fn number(&self) -> Option<f64> {
        match self {
            Self::Int(v) => Some(*v as f64),
            Self::Float(v) => Some(*v),
            _ => None,
        }
    }
    fn integer(&self) -> Option<i64> {
        self.number().map(|v| v as i64)
    }
    fn truth(&self) -> bool {
        match self {
            Self::Nil | Self::Fault(_) => false,
            Self::Bool(v) => *v,
            Self::Int(v) => *v != 0,
            Self::Float(v) => *v != 0.,
            Self::Text(v) => !v.is_empty(),
            Self::Array(v) => !v.is_empty(),
        }
    }
    fn to_float(&self) -> Option<f64> {
        match self {
            Self::Bool(v) => Some(u8::from(*v) as f64),
            Self::Text(v) => Some(string_number(v)),
            Self::Nil => None,
            Self::Fault(value) => value.to_float(),
            _ => Some(self.number().unwrap_or(0.)),
        }
    }
}
/// Original Range snaps relative to min with floor(x+0.5), then clamps. This
/// intentionally differs from round() for a negative half and maps NaN to min.
pub(super) fn clamp(value: f64, maximum: u16) -> u16 {
    let snapped = (value - 1. + 0.5).floor() + 1.;
    if snapped.is_nan() || snapped < 1. {
        1
    } else if snapped > f64::from(maximum.max(1)) {
        maximum.max(1)
    } else {
        snapped as u16
    }
}
pub(super) fn committed(text: &str, old: u16, maximum: u16) -> u16 {
    // Bound local editor work, not wire or inventory state. The original input
    // field has no useful reason to build a huge expression tree.
    if text.len() > 4096 {
        return old;
    }
    parser::evaluate(&text.replace(';', ","))
        .and_then(|value| value.to_float())
        .map_or(old, |value| clamp(value, maximum))
}

// Godot String::to_float consumes the numeric prefix (e.g. "12x" -> 12).
fn string_number(text: &str) -> f64 {
    let text = text.trim_start();
    let mut end = 0;
    for (i, c) in text.char_indices() {
        if c.is_ascii_digit() || matches!(c, '.' | 'e' | 'E' | '+' | '-') {
            end = i + c.len_utf8();
        } else {
            break;
        }
    }
    while end > 0 {
        if let Ok(value) = text[..end].parse() {
            return value;
        }
        end -= 1;
    }
    0.
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_godot_spinbox_oracle_keeps_numeric_types_precedence_and_failure_behavior() {
        let rows: Vec<serde_json::Value> =
            serde_json::from_str(include_str!("expression/source-reference.json")).unwrap();
        assert_eq!(rows.len(), 241);
        for row in rows {
            let text = row["text"].as_str().unwrap();
            assert_eq!(
                committed(text, 17, 499),
                row["value"].as_u64().unwrap() as u16,
                "{text}"
            );
        }
    }
    #[test]
    fn local_expression_work_is_bounded_and_never_panics_on_incomplete_editor_drafts() {
        for text in [
            "(",
            "[",
            "2+",
            "'\\u",
            "1e+",
            "min(1,",
            "int([4])",
            "1<<999999999999",
            "9223372036854775807+1",
        ] {
            let _ = committed(text, 17, 499);
        }
        assert_eq!(committed(&"(".repeat(200), 17, 499), 17);
        assert_eq!(committed(&"2+".repeat(3000), 17, 499), 17);
        assert_eq!(committed("min(3;8)+pow(2;3)", 17, 499), 11);
        assert_eq!(committed("7/2", 17, 2), 2);
        assert_eq!(committed("null", 17, 499), 17);
    }
}
