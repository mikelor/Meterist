dotnet run --project ../src/Meterist.Cli -- credentials set --tenant zelleri --vendor gemini-enterprise --from-file "..\env\zelleri-gemini-credentials.json"

dotnet run --project ../src/Meterist.Cli -- extract --tenant zelleri --from 2026-07-01 --to 2026-07-22 --vendor gemini-enterprise