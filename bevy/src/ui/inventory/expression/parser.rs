use super::{
    Value, functions,
    lexer::{Lexer, Token},
};
#[derive(Clone)]
enum Node {
    Value(Value),
    Op(&'static str),
}
struct Parser {
    lexer: Lexer,
    next: Token,
    depth: usize,
}
pub(super) fn evaluate(text: &str) -> Option<Value> {
    let mut lexer = Lexer::new(text);
    let next = lexer.next()?;
    Parser {
        lexer,
        next,
        depth: 0,
    }
    .expression()
}
impl Parser {
    fn take(&mut self) -> Option<Token> {
        let next = self.lexer.next()?;
        Some(std::mem::replace(&mut self.next, next))
    }
    fn mark(&mut self, c: char) -> Option<()> {
        if self.next != Token::Mark(c) {
            return None;
        }
        self.take()?;
        Some(())
    }
    fn list(&mut self, end: char) -> Option<Vec<Value>> {
        let mut result = Vec::new();
        while self.next != Token::Mark(end) {
            if result.len() >= 256 {
                return None;
            }
            result.push(self.expression()?);
            if self.next == Token::Mark(end) {
                break;
            }
            self.mark(',')?;
        }
        self.mark(end)?;
        Some(result)
    }
    fn expression(&mut self) -> Option<Value> {
        self.depth += 1;
        if self.depth > 64 {
            return None;
        }
        let mut nodes = Vec::new();
        loop {
            if nodes.len() > 1024 {
                return None;
            }
            let value = match self.take()? {
                Token::Op(op) if matches!(op.as_str(), "-" | "!" | "not") => {
                    nodes.push(Node::Op(match op.as_str() {
                        "-" => "neg",
                        "~" => "~",
                        _ => "not",
                    }));
                    continue;
                }
                Token::Value(value) => value,
                Token::Mark('(') => {
                    let value = self.expression()?;
                    self.mark(')')?;
                    value
                }
                Token::Mark('[') => {
                    let values = self.list(']')?;
                    if values.iter().any(|v| matches!(v, Value::Fault(_))) {
                        Value::Fault(Box::new(Value::Nil))
                    } else {
                        Value::Array(values)
                    }
                }
                Token::Name(name) => {
                    self.mark('(')?;
                    let args = self.list(')')?;
                    functions::call(&name, &args)?
                }
                _ => return None,
            };
            let mut value = value;
            while self.next == Token::Mark('[') {
                self.take()?;
                let index = self.expression()?.integer()?;
                self.mark(']')?;
                value = match value {
                    Value::Fault(_) => Value::Fault(Box::new(Value::Nil)),
                    Value::Array(values) => {
                        let index = if index < 0 {
                            values.len() as i64 + index
                        } else {
                            index
                        };
                        values.get(usize::try_from(index).ok()?)?.clone()
                    }
                    Value::Text(text) => {
                        let chars: Vec<_> = text.chars().collect();
                        let index = if index < 0 {
                            chars.len() as i64 + index
                        } else {
                            index
                        };
                        Value::Text(chars.get(usize::try_from(index).ok()?)?.to_string())
                    }
                    _ => return None,
                };
            }
            nodes.push(Node::Value(value));
            let Token::Op(op) = &self.next else {
                break;
            };
            let op = binary_op(op)?;
            self.take()?;
            nodes.push(Node::Op(op));
        }
        // Original Expression reduces the leftmost operator with smallest
        // precedence. Power is left associative; a unary after power is invalid.
        while nodes.len() > 1 {
            let (at, op) = nodes
                .iter()
                .enumerate()
                .filter_map(|(i, node)| match node {
                    Node::Op(op) => Some((i, *op)),
                    _ => None,
                })
                .min_by_key(|(_, op)| priority(op))?;
            if matches!(op, "neg" | "~" | "not") {
                let end = (at + 1..nodes.len()).find(|i| matches!(nodes[*i], Node::Value(_)))?;
                let Node::Value(mut value) = nodes[end].clone() else {
                    return None;
                };
                for i in (at..end).rev() {
                    let Node::Op(op) = nodes[i] else {
                        return None;
                    };
                    value = unary(op, value)?;
                }
                nodes.splice(at..=end, [Node::Value(value)]);
            } else {
                if at == 0 || at + 1 >= nodes.len() {
                    return None;
                }
                let (Node::Value(left), Node::Value(right)) =
                    (nodes[at - 1].clone(), nodes[at + 1].clone())
                else {
                    return None;
                };
                let value = apply(op, left, right)?;
                nodes.splice(at - 1..=at + 1, [Node::Value(value)]);
            }
        }
        self.depth -= 1;
        match nodes.pop()? {
            Node::Value(value) => Some(value),
            _ => None,
        }
    }
}
fn binary_op(op: &str) -> Option<&'static str> {
    Some(match op {
        "**" => "**",
        "+" => "+",
        "-" => "-",
        "*" => "*",
        "/" => "/",
        "%" => "%",
        "<<" => "<<",
        ">>" => ">>",
        "&" => "&",
        "^" => "^",
        "|" => "|",
        "==" => "==",
        "!=" => "!=",
        "<" => "<",
        "<=" => "<=",
        ">" => ">",
        ">=" => ">=",
        "in" => "in",
        "not" | "!" => "not",
        "and" | "&&" => "and",
        "or" | "||" => "or",
        "~" => "~",
        _ => return None,
    })
}
fn priority(op: &str) -> u8 {
    match op {
        "**" => 0,
        "~" => 1,
        "neg" => 2,
        "*" | "/" | "%" => 3,
        "+" | "-" => 4,
        "<<" | ">>" => 5,
        "&" => 6,
        "^" => 7,
        "|" => 8,
        "==" | "!=" | "<" | "<=" | ">" | ">=" => 9,
        "in" => 11,
        "not" => 12,
        "and" => 13,
        _ => 14,
    }
}
fn unary(op: &str, value: Value) -> Option<Value> {
    Some(match (op, value) {
        (_, Value::Fault(_)) => Value::Fault(Box::new(Value::Nil)),
        ("neg", Value::Int(v)) => Value::Int(v.wrapping_neg()),
        ("neg", Value::Float(v)) => Value::Float(-v),
        ("~", Value::Int(v)) => Value::Int(!v),
        ("not", v) => Value::Bool(!v.truth()),
        _ => return None,
    })
}
fn apply(op: &str, a: Value, b: Value) -> Option<Value> {
    use Value::*;
    if matches!(a, Fault(_)) || matches!(b, Fault(_)) {
        return Some(Fault(Box::new(Nil)));
    }
    if op == "and" {
        return Some(Bool(a.truth() && b.truth()));
    }
    if op == "or" {
        return Some(Bool(a.truth() || b.truth()));
    }
    if op == "in" {
        return Some(Bool(match b {
            Array(values) => values.contains(&a),
            Text(text) => {
                if let Text(needle) = a {
                    text.contains(&needle)
                } else {
                    return None;
                }
            }
            _ => return None,
        }));
    }
    if matches!(op, "==" | "!=") {
        let same = match (a.number(), b.number()) {
            (Some(a), Some(b)) => a == b,
            _ => a == b,
        };
        return Some(Bool(if op == "==" { same } else { !same }));
    }
    if let (Text(a), Text(b)) = (&a, &b) {
        return Some(match op {
            "+" => Text(format!("{a}{b}")),
            "<" => Bool(a < b),
            "<=" => Bool(a <= b),
            ">" => Bool(a > b),
            ">=" => Bool(a >= b),
            _ => return None,
        });
    }
    if let (Array(a), Array(b)) = (&a, &b) {
        if op == "+" {
            return Some(Array(a.iter().chain(b).cloned().collect()));
        }
    }
    if let (Int(a), Int(b)) = (&a, &b) {
        if matches!(op, "/" | "%") && *b == 0 {
            return Some(Fault(Box::new(Text("Division by zero".into()))));
        }
        return Some(match op {
            "+" => Int(a.wrapping_add(*b)),
            "-" => Int(a.wrapping_sub(*b)),
            "*" => Int(a.wrapping_mul(*b)),
            "/" => Int(a.checked_div(*b).unwrap_or(0)),
            "%" => Int(a.checked_rem(*b).unwrap_or(0)),
            "**" => Int((*a as f64).powf(*b as f64) as i64),
            "<<" => Int(a.wrapping_shl(*b as u32)),
            ">>" => Int(a.wrapping_shr(*b as u32)),
            "&" => Int(a & b),
            "|" => Int(a | b),
            "^" => Int(a ^ b),
            "<" => Bool(a < b),
            "<=" => Bool(a <= b),
            ">" => Bool(a > b),
            ">=" => Bool(a >= b),
            _ => return None,
        });
    }
    let (a, b) = (a.number()?, b.number()?);
    Some(match op {
        "+" => Float(a + b),
        "-" => Float(a - b),
        "*" => Float(a * b),
        "/" => Float(a / b),
        "**" => Float(a.powf(b)),
        "<" => Bool(a < b),
        "<=" => Bool(a <= b),
        ">" => Bool(a > b),
        ">=" => Bool(a >= b),
        _ => return None,
    })
}
