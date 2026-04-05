var b = api.Symbols.GetModules()[0].BaseAddress;
api.Symbols.RegisterFunction(b + 0x1470, "DecryptStringRc4");
api.Symbols.RegisterFunction(b + 0x1370, "PritnFunc");
api.UI.RefreshDisassembly();
