use edgelet_http::route::*;
use futures::{future, Future};

pub struct WorkloadService {
    inner: RouterService<RegexRecognizer>,
}

impl WorkloadService {
    // clippy bug: https://github.com/rust-lang-nursery/rust-clippy/issues/3220
    #[cfg_attr(feature = "cargo-clippy", allow(new_ret_no_self))]
    pub fn new() -> impl Future<Item = Self, Error = Error> {}
}
