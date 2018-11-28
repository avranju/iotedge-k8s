// Copyright (c) Microsoft. All rights reserved.

use std::marker::PhantomData;

use error::Error;
use module::{Module, ModuleRuntime, ModuleSpec};

use futures::{future, Future};

pub struct Watchdog<M, I> {
    runtime: PhantomData<M>,
    identity: PhantomData<I>,
}

impl<M, I> Watchdog<M, I>
where
    M: 'static + ModuleRuntime + Clone,
{
    pub fn new(_: M, _: I) -> Self {
        Watchdog {
            runtime: PhantomData,
            identity: PhantomData,
        }
    }

    pub fn run_until<F>(
        self,
        _spec: ModuleSpec<<M::Module as Module>::Config>,
        _module_id: &str,
        _shutdown_signal: F,
    ) -> impl Future<Item = (), Error = Error>
    where
        F: Future<Item = (), Error = ()> + 'static,
    {
        future::ok(())
    }
}

pub fn start_watchdog<M, I>(
    _runtime: M,
    _id_mgr: I,
    _spec: ModuleSpec<<M::Module as Module>::Config>,
    _module_id: String,
) -> impl Future<Item = (), Error = Error>
where
    M: 'static + ModuleRuntime,
{
    future::ok(())
}
