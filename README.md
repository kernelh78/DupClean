현재 프로젝트 구조

```
src/
├── DupClean.Core/
│   ├── Actions/
│   │   ├── DeleteAction.cs          ✅
│   │   ├── QuarantineAction.cs      ✅
│   │   └── IDuplicationAction.cs    ✅
│   ├── Data/
│   │   ├── DupCleanDbContext.cs     ✅
│   │   ├── DupCleanServiceFactory.cs ✅
│   │   └── UndoManager.cs           ✅
│   ├── Detection/
│   │   ├── DuplicateEngine.cs       ✅
│   │   └── SmartSelector.cs         ✅ (신규)
│   ├── Hashing/
│   │   ├── PHashCalculator.cs       ✅
│   │   ├── PHashCache.cs            ✅
│   │   └── SparseHasher.cs          ✅
│   ├── Models/
│   │   ├── DuplicateGroup.cs        ✅
│   │   ├── FileEntry.cs             ✅
│   │   ├── FileHash.cs              ✅
│   │   └── ScanOptions.cs           ✅
│   └── Scanning/
│       ├── FileScannerFactory.cs    ✅
│       └── IoScanner.cs             ✅
├── DupClean.UI/
│   ├── App.xaml.cs                  ✅
│   ├── Converters/                  ✅
│   ├── ViewModels/
│   │   └── MainViewModel.cs         ✅
│   └── Views/
│       └── MainWindow.xaml          ✅
└── DupClean.Tests/
    ├── DuplicateEngineTests.cs      ✅
    ├── PHashCalculatorTests.cs      ✅
    ├── SmartSelectorTests.cs        ✅ (신규)
    └── SparseHasherTests.cs         ✅
