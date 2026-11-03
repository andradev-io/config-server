using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigServer.Domain.Security.Identity
{
    /// <summary>
    /// Manages who holds which config grants by driving the identity provider's admin API. The identity provider stays the
    /// source of truth: a grant is a client role named <c>config:&lt;perm&gt;[:&lt;app&gt;[:&lt;env&gt;]]</c> (see
    /// <see cref="ConfigGrant"/>) on the config client, mapped to a user or a group. Changes reach a subject's token at its
    /// next token refresh or sign-in. Failures throw <see cref="IdentityAdminException"/>.
    /// </summary>
    public interface IIdentityAdmin
    {
        /// <summary>Provider name and whether management is enabled (no network call).</summary>
        Task<IdentityAdminInfo> GetInfoAsync(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string search, int first, int max, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string search, int first, int max, CancellationToken cancellationToken = default);

        /// <summary>
        /// The subject's direct config-grant role mappings on the config client; for users also the ones inherited from
        /// their groups (and parent groups), flagged <see cref="SubjectGrant.Inherited"/> with the group name in
        /// <see cref="SubjectGrant.Via"/>. Roles that do not parse as a config grant are left out.
        /// </summary>
        Task<IReadOnlyList<SubjectGrant>> GetGrantsAsync(SubjectType type, string subjectId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensures the client role <see cref="ConfigGrant.RoleName"/> exists (creates it when missing) and maps it to the
        /// subject. Idempotent: an existing direct mapping carrying the same grant (any spelling) is returned as is.
        /// </summary>
        Task<SubjectGrant> GrantAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the subject's direct mappings of roles carrying <paramref name="grant"/>. The roles themselves are kept.
        /// Returns false when there was nothing to remove (idempotent). Inherited grants are not touched.
        /// </summary>
        Task<bool> RevokeAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default);

        /// <summary>
        /// The machine credentials this server created ("OIDC API keys"). Clients it did not create are never returned.
        /// </summary>
        Task<IReadOnlyList<MachineClient>> ListMachineClientsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a confidential client with a service account, grants it <paramref name="grants"/> (admin is refused) and
        /// returns it with its secret (only returned here and on rotation).
        /// </summary>
        Task<MachineClientSecret> CreateMachineClientAsync(string name, IReadOnlyList<ConfigGrant> grants, CancellationToken cancellationToken = default);

        /// <summary>Issues a new secret (the old one stops working). <see cref="IdentityAdminErrorKind.NotFound"/> for unknown or unmanaged clients.</summary>
        Task<MachineClientSecret> RotateMachineClientSecretAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>Deletes the credential. False when it does not exist (idempotent); unmanaged clients are never deleted.</summary>
        Task<bool> DeleteMachineClientAsync(string id, CancellationToken cancellationToken = default);
    }

    /// <summary>IdentityAdmin:Provider=None: management is off and every operation but <see cref="GetInfoAsync"/> throws.</summary>
    public sealed class DisabledIdentityAdmin : IIdentityAdmin
    {
        public const string ProviderName = "none";

        public const string DisabledMessage =
            "Identity management is not configured on this server (IdentityAdmin:Provider=None). " +
            "Set IdentityAdmin:Provider=Keycloak and the IdentityAdmin:Keycloak settings to manage grants from here.";

        private static readonly IdentityAdminInfo Info = new IdentityAdminInfo(ProviderName, false, null, DisabledMessage);

        public Task<IdentityAdminInfo> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);

        public Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string search, int first, int max, CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string search, int first, int max, CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<IReadOnlyList<SubjectGrant>> GetGrantsAsync(SubjectType type, string subjectId, CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<SubjectGrant> GrantAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<bool> RevokeAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<IReadOnlyList<MachineClient>> ListMachineClientsAsync(CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<MachineClientSecret> CreateMachineClientAsync(string name, IReadOnlyList<ConfigGrant> grants, CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<MachineClientSecret> RotateMachineClientSecretAsync(string id, CancellationToken cancellationToken = default)
            => throw Disabled();

        public Task<bool> DeleteMachineClientAsync(string id, CancellationToken cancellationToken = default)
            => throw Disabled();

        private static IdentityAdminException Disabled() => new IdentityAdminException(IdentityAdminErrorKind.NotConfigured, DisabledMessage);
    }
}
