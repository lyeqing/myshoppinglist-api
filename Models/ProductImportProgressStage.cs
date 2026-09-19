namespace myshoppinglist_api.Models;

public enum ProductImportProgressStage
{
    Queued = 0,
    ValidatingUrl = 1,
    ReadingSourceProduct = 2,
    IdentifyingProduct = 3,
    SavingSourceProduct = 4,
    AddingToShoppingList = 5,
    CheckingRetailers = 6,
    SavingPrices = 7,
    Completed = 8,
    Failed = 9,
}

