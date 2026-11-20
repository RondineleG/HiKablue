fn a() {
    1 + 1;
}
pub fn c() {}
// coverage: lab-only begin
pub fn b() {}
// coverage: lab-only end
#[cfg(test)]
mod tests { }
