/// Puts the Claude GitHub App's private key into Key Vault (D52) and lets the agents sign with it.
module Platform.Access.ClaudeApp

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative.Authorization
open Pulumi.AzureNative.ManagedIdentity
open Pulumi.Command.Local
open Platform.Access.Desired

let private cryptoOfficer = "14b46e9e-c2b7-41b4-b07b-48a6ebf60603"
let private cryptoUser = "12338af0-0e69-4776-bea7-57ae8d297424"

type Deployed =
    {
        importer: RoleAssignment
        key: Command
        signers: RoleAssignment list
    }

let deploy
    (subscriptionId: string)
    (resourceGroup: string)
    (vault: string)
    (script: string)
    (groups: Map<GroupName, Pulumi.AzureAD.Group>)
    (k: ClaudeAppKey)
    : Deployed =
    let role id =
        $"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{id}"

    let vaultScope =
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.KeyVault/vaults/{vault}"

    let applyIdentity =
        GetUserAssignedIdentity
            .Invoke(
                GetUserAssignedIdentityInvokeArgs(
                    ResourceGroupName = resourceGroup,
                    ResourceName = CiIdentity.name ApplyIdentity
                )
            )
            .Apply(_.PrincipalId)

    // The apply identity imports the key. Key Vault roles can't be scoped to a key that doesn't exist yet,
    // so this one is on the vault; the apply identity already holds Contributor on the subscription.
    let importer =
        RoleAssignment(
            "apply-key-vault-crypto-officer",
            RoleAssignmentArgs(
                Scope = vaultScope,
                RoleDefinitionId = role cryptoOfficer,
                PrincipalId = io applyIdentity,
                PrincipalType = PrincipalType.ServicePrincipal,
                Description = "id-platform-apply: imports the Claude GitHub App key (D52)"
            )
        )

    let key =
        Command(
            "claude-app-key",
            CommandArgs(
                Create = script,
                Interpreter = inputList [ input "/bin/bash"; input "-c" ],
                Environment = inputMap [ "VAULT", input vault; "KEY_NAME", input k.keyName ],
                Triggers = inputList [ input (box vault); input (box k.keyName) ]
            ),
            CustomResourceOptions(DependsOn = inputList [ input (importer :> Resource) ])
        )

    // Key Vault Crypto User on this one key only. The key's only permitted operation is sign.
    let signers =
        k.signers
        |> List.map (fun signer ->
            RoleAssignment(
                $"{GroupName.value signer}-claude-app-key-signer",
                RoleAssignmentArgs(
                    Scope = $"{vaultScope}/keys/{k.keyName}",
                    RoleDefinitionId = role cryptoUser,
                    PrincipalId = io groups[signer].ObjectId,
                    PrincipalType = PrincipalType.Group,
                    Description = $"{GroupName.value signer}: sign with the Claude GitHub App key (D52)"
                ),
                CustomResourceOptions(DependsOn = inputList [ input (key :> Resource) ])
            ))

    {
        importer = importer
        key = key
        signers = signers
    }
