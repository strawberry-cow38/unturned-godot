use super::Value;
#[derive(Clone, Debug, PartialEq)]
pub(super) enum Token {
    Value(Value),
    Name(String),
    Op(String),
    Mark(char),
    End,
}
pub(super) struct Lexer {
    chars: Vec<char>,
    position: usize,
}
impl Lexer {
    pub fn new(text: &str) -> Self {
        Self {
            chars: text.chars().collect(),
            position: 0,
        }
    }
    fn peek(&self) -> char {
        self.chars.get(self.position).copied().unwrap_or('\0')
    }
    fn take(&mut self) -> char {
        let c = self.peek();
        if c != '\0' {
            self.position += 1;
        }
        c
    }
    pub fn next(&mut self) -> Option<Token> {
        while self.peek() != '\0' && self.peek() <= ' ' {
            self.take();
        }
        let c = self.take();
        if c == '\0' {
            return Some(Token::End);
        }
        if c == '\'' || c == '"' {
            let mut value = String::new();
            loop {
                let ch = self.take();
                if ch == '\0' {
                    return None;
                }
                if ch == c {
                    break;
                }
                if ch != '\\' {
                    value.push(ch);
                    continue;
                }
                let escape = self.take();
                value.push(match escape {
                    '\0' => return None,
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    'b' => '\u{8}',
                    'f' => '\u{c}',
                    'u' | 'U' => {
                        let count = if escape == 'u' { 4 } else { 6 };
                        let mut hex = String::new();
                        for _ in 0..count {
                            hex.push(self.take());
                        }
                        char::from_u32(u32::from_str_radix(&hex, 16).ok()?)?
                    }
                    other => other,
                });
            }
            return Some(Token::Value(Value::Text(value)));
        }
        if c.is_ascii_digit() || c == '.' && self.peek().is_ascii_digit() {
            let mut number = String::from(c);
            if c == '0' && matches!(self.peek(), 'x' | 'X' | 'b' | 'B') {
                let radix = if matches!(self.take(), 'x' | 'X') {
                    16
                } else {
                    2
                };
                number.clear();
                while self.peek().is_digit(radix) {
                    number.push(self.take());
                }
                let value = u64::from_str_radix(&number, radix).unwrap_or(0) as i64;
                return Some(Token::Value(Value::Int(value)));
            }
            while self.peek().is_ascii_digit() {
                number.push(self.take());
            }
            let mut float = c == '.';
            if !float && self.peek() == '.' {
                float = true;
                number.push(self.take());
                while self.peek().is_ascii_digit() {
                    number.push(self.take());
                }
            }
            if matches!(self.peek(), 'e' | 'E') {
                float = true;
                number.push(self.take());
                if matches!(self.peek(), '-' | '+') {
                    number.push(self.take());
                }
                while self.peek().is_ascii_digit() {
                    number.push(self.take());
                }
            }
            let value = if float {
                // String::to_float accepts an exponent prefix with no digits.
                let mut valid = number.as_str();
                while valid.ends_with(['e', 'E', '+', '-']) {
                    valid = &valid[..valid.len() - 1];
                }
                Value::Float(valid.parse().unwrap_or(0.))
            } else {
                Value::Int(number.parse().unwrap_or(i64::MAX))
            };
            return Some(Token::Value(value));
        }
        if c.is_alphabetic() || c == '_' {
            let mut name = String::from(c);
            while self.peek().is_alphanumeric() || self.peek() == '_' {
                name.push(self.take());
            }
            return Some(match name.as_str() {
                "and" | "or" | "not" | "in" => Token::Op(name),
                "true" => Token::Value(Value::Bool(true)),
                "false" => Token::Value(Value::Bool(false)),
                "null" => Token::Value(Value::Nil),
                "PI" => Token::Value(Value::Float(std::f64::consts::PI)),
                "TAU" => Token::Value(Value::Float(std::f64::consts::TAU)),
                "INF" => Token::Value(Value::Float(f64::INFINITY)),
                "NAN" => Token::Value(Value::Float(f64::NAN)),
                _ => Token::Name(name),
            });
        }
        if "()[],.:{}".contains(c) {
            return Some(Token::Mark(c));
        }
        if "+-*/%&|^~!=<>".contains(c) {
            let mut op = String::from(c);
            if matches!(
                (c, self.peek()),
                ('*', '*')
                    | ('&', '&')
                    | ('|', '|')
                    | ('!', '=')
                    | ('=', '=')
                    | ('<', '=')
                    | ('>', '=')
                    | ('<', '<')
                    | ('>', '>')
            ) {
                op.push(self.take());
            }
            if op == "=" {
                return None;
            }
            return Some(Token::Op(op));
        }
        None
    }
}
