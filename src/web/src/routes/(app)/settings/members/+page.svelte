<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
  import { invalidateAll } from "$app/navigation";
  import { ApiClient } from "$lib/api/client";
  import type {
    InvitationListDto,
    MemberDto,
    SubscriptionDto,
  } from "$lib/api/types";
  import {
    Mail,
    Copy,
    RefreshCw,
    XCircle,
    UserMinus,
    Check,
  } from "lucide-svelte";
  import PageHeader from '$lib/components/PageHeader.svelte';
  import ConfirmDialog from '$lib/components/ConfirmDialog.svelte';
  import { formatDate } from '$lib/utils/format';

  let { data } = $props();

  let invitations: InvitationListDto[] = $derived(data.invitations);
  let members: MemberDto[] = $derived(data.members);
  let subscription: SubscriptionDto | null = $derived(data.subscription);
  const isFreeTier = $derived(subscription === null || subscription.tier === "Free");
  const isTeamTier = $derived(subscription !== null && subscription.tier === "Team");

  let inviteEmail = $state("");
  let inviteRole = $state("Viewer");
  let inviteError = $state("");
  let inviteSuccess = $state("");
  let inviteLoading = $state(false);
  let copiedUrl = $state("");
  let confirmRemoveUserId = $state<number | null>(null);
  let revokeInviteConfirm = $state<{ open: boolean; id: number | null }>({ open: false, id: null });

  const client = new ApiClient("");

  async function sendInvitation() {
    inviteError = "";
    inviteSuccess = "";

    if (!inviteEmail.trim() || !inviteEmail.includes("@")) {
      inviteError = "Please enter a valid email address.";

      return;
    }

    inviteLoading = true;
    try {
      const roleToSend = isTeamTier ? inviteRole : "TenantAdmin";
      const result = await client.createInvitation(
        inviteEmail.trim(),
        roleToSend,
      );
      inviteSuccess = `Invitation sent to ${inviteEmail}`;
      copiedUrl = result.acceptUrl;
      inviteEmail = "";
      await invalidateAll();
    } catch (err: unknown) {
      inviteError =
        err instanceof Error ? err.message : "Failed to send invitation";
    } finally {
      inviteLoading = false;
    }
  }

  async function changeMemberRole(userId: number, role: string) {
    try {
      await client.changeMemberRole(userId, role);
      await invalidateAll();
    } catch (err: unknown) {
      inviteError =
        err instanceof Error ? err.message : "Failed to change member role";
    }
  }

  async function revokeInvitation(id: number) {
    try {
      await client.revokeInvitation(id);
      await invalidateAll();
    } catch (err: unknown) {
      inviteError =
        err instanceof Error ? err.message : "Failed to revoke invitation";
    }
  }

  async function resendInvitation(id: number) {
    try {
      await client.resendInvitation(id);
      await invalidateAll();
      inviteSuccess = "Invitation resent successfully";
    } catch (err: unknown) {
      inviteError =
        err instanceof Error ? err.message : "Failed to resend invitation";
    }
  }

  async function removeMember(userId: number) {
    try {
      await client.removeMember(userId);
      confirmRemoveUserId = null;
      await invalidateAll();
    } catch (err: unknown) {
      inviteError =
        err instanceof Error ? err.message : "Failed to remove member";
    }
  }

  function copyToClipboard(text: string) {
    navigator.clipboard.writeText(text);
  }

  function isExpired(expiresAt: string): boolean {
    return new Date(expiresAt) < new Date();
  }

  const pendingInvitations = $derived(
    invitations.filter((i) => i.status === "Pending" || i.status === "Expired"),
  );
  const pastInvitations = $derived(
    invitations.filter(
      (i) => i.status === "Accepted" || i.status === "Revoked",
    ),
  );
</script>

<svelte:head><title>Team Members - Vord</title></svelte:head>

<div class="mx-auto max-w-4xl space-y-6">
  <PageHeader title="Team Members" description="Manage your organization's team members and invitations." />

  <!-- Invite form -->
  <div
    class="mt-8 rounded-lg border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
  >
    <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
      Invite a Team Member
    </h2>
    <p class="mt-1 text-sm text-surface-500">
      Send an invitation by email. The invitee must sign in with a matching
      email address.
    </p>

    {#if isFreeTier}
      <div
        class="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-700 dark:bg-amber-900/20"
      >
        <p class="text-sm text-amber-800 dark:text-amber-300">
          Upgrade to Pro to invite team members — <strong>$3/host/month</strong>
        </p>
        <a
          href="/settings/billing"
          class="mt-2 inline-block rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600"
        >
          Upgrade Now
        </a>
      </div>
      <form
        class="mt-4 flex gap-3 opacity-50 pointer-events-none"
      >
        <input
          type="email"
          disabled
          placeholder="colleague@company.com"
          class="flex-1 rounded-lg border border-surface-300 bg-surface-50 px-4 py-2 text-sm text-surface-900 placeholder:text-surface-400 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
        />
        <button
          type="button"
          disabled
          class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white opacity-50"
        >
          <Mail size={16} />
          Send Invitation
        </button>
      </form>
    {:else}
      <form
        onsubmit={(e) => {
          e.preventDefault();
          sendInvitation();
        }}
        class="mt-4 flex gap-3"
      >
        <input
          type="email"
          bind:value={inviteEmail}
          placeholder="colleague@company.com"
          class="flex-1 rounded-lg border border-surface-300 bg-surface-50 px-4 py-2 text-sm text-surface-900 placeholder:text-surface-400 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
        />
        {#if isTeamTier}
          <select
            bind:value={inviteRole}
            class="rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm text-surface-900 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
          >
            <option value="Viewer">Viewer</option>
            <option value="MachineAdmin">MachineAdmin</option>
            <option value="TenantAdmin">TenantAdmin</option>
          </select>
        {/if}
        <button
          type="submit"
          disabled={inviteLoading}
          class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600 disabled:opacity-50"
        >
          <Mail size={16} />
          {inviteLoading ? "Sending..." : "Send Invitation"}
        </button>
      </form>
    {/if}

    {#if inviteError}
      <p class="mt-3 text-sm text-red-600 dark:text-red-400">{inviteError}</p>
    {/if}

    {#if inviteSuccess}
      <div
        class="mt-3 flex items-center gap-2 rounded-lg bg-green-50 px-4 py-3 dark:bg-green-900/20"
      >
        <Check size={16} class="text-green-600 dark:text-green-400" />
        <span class="text-sm text-green-700 dark:text-green-300"
          >{inviteSuccess}</span
        >
        {#if copiedUrl}
          <button
            onclick={() => copyToClipboard(copiedUrl)}
            class="ml-auto inline-flex items-center gap-1 rounded px-2 py-1 text-xs text-green-700 hover:bg-green-100 dark:text-green-300 dark:hover:bg-green-800/30"
          >
            <Copy size={12} />
            Copy Link
          </button>
        {/if}
      </div>
    {/if}
  </div>

  <!-- Active members -->
  <div
    class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
  >
    <div class="border-b border-surface-200 px-6 py-4 dark:border-surface-700">
      <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
        Active Members <span class="text-xs font-normal text-surface-400">({members.length})</span>
      </h2>
    </div>
    {#if members.length === 0}
      <p class="px-6 py-8 text-center text-sm text-surface-500">
        No members yet.
      </p>
    {:else}
      <div class="divide-y divide-surface-100 dark:divide-surface-700">
        {#each members as member (member.userId)}
          <div class="flex items-center justify-between px-6 py-4">
            <div>
              <p
                class="text-sm font-medium text-surface-900 dark:text-surface-50"
              >
                {member.email}
              </p>
              <p class="text-xs text-surface-500">
                Joined {formatDate(member.joinedAt)}
              </p>
            </div>
            {#if member.userId !== data.user.id}
              <div class="flex items-center gap-2">
                {#if isTeamTier}
                  <select
                    value={member.role}
                    onchange={(e) =>
                      changeMemberRole(
                        member.userId,
                        (e.currentTarget as HTMLSelectElement).value,
                      )}
                    class="rounded-lg border border-surface-300 bg-surface-50 px-2 py-1 text-xs text-surface-900 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
                  >
                    <option value="Viewer">Viewer</option>
                    <option value="MachineAdmin">MachineAdmin</option>
                    <option value="TenantAdmin">TenantAdmin</option>
                  </select>
                {:else}
                  <span
                    class="rounded-full bg-surface-100 px-2 py-0.5 text-xs font-medium text-surface-600 dark:bg-surface-700 dark:text-surface-400"
                    >{member.role}</span
                  >
                {/if}
                {#if confirmRemoveUserId === member.userId}
                  <div class="flex items-center gap-2">
                    <span class="text-xs text-surface-500">Remove?</span>
                    <button
                      onclick={() => removeMember(member.userId)}
                      class="rounded bg-red-500 px-3 py-1 text-xs font-medium text-white hover:bg-red-600"
                    >
                      Yes
                    </button>
                    <button
                      onclick={() => (confirmRemoveUserId = null)}
                      class="rounded border border-surface-300 px-3 py-1 text-xs font-medium text-surface-700 hover:bg-surface-50 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
                    >
                      No
                    </button>
                  </div>
                {:else}
                  <button
                    onclick={() => (confirmRemoveUserId = member.userId)}
                    class="inline-flex items-center gap-1 rounded-md border border-surface-200 px-2.5 py-1 text-xs text-surface-500 hover:bg-surface-100 hover:text-red-600 dark:border-surface-700 dark:hover:bg-surface-700 dark:hover:text-red-400"
                    title="Remove member"
                  >
                    <UserMinus size={14} />
                    Remove
                  </button>
                {/if}
              </div>
            {:else}
              <span class="text-xs text-surface-400">You</span>
            {/if}
          </div>
        {/each}
      </div>
    {/if}
  </div>

  <!-- Pending invitations -->
  {#if pendingInvitations.length > 0}
    <div
      class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
    >
      <div
        class="border-b border-surface-200 px-6 py-4 dark:border-surface-700"
      >
        <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
          Pending Invitations <span class="text-xs font-normal text-surface-400">({pendingInvitations.length})</span>
        </h2>
      </div>
      <div class="divide-y divide-surface-100 dark:divide-surface-700">
        {#each pendingInvitations as inv (inv.id)}
          <div class="flex items-center justify-between px-6 py-4">
            <div>
              <p
                class="text-sm font-medium text-surface-900 dark:text-surface-50"
              >
                {inv.email}
              </p>
              <p class="text-xs text-surface-500">
                Sent {formatDate(inv.createdAt)} &middot;
                {#if inv.status === "Expired" || isExpired(inv.expiresAt)}
                  <span class="text-amber-600 dark:text-amber-400">Expired</span
                  >
                {:else}
                  Expires {formatDate(inv.expiresAt)}
                {/if}
              </p>
            </div>
            <div class="flex items-center gap-2">
              <button
                onclick={() => resendInvitation(inv.id)}
                class="inline-flex items-center gap-1 rounded-md border border-surface-200 px-2.5 py-1 text-xs text-surface-500 hover:bg-surface-100 dark:border-surface-700 dark:hover:bg-surface-700"
                title="Resend invitation"
              >
                <RefreshCw size={14} />
                Resend
              </button>
              <button
                onclick={() => revokeInviteConfirm = { open: true, id: inv.id }}
                class="inline-flex items-center gap-1 rounded-md border border-surface-200 px-2.5 py-1 text-xs text-surface-500 hover:bg-surface-100 hover:text-red-600 dark:border-surface-700 dark:hover:bg-surface-700 dark:hover:text-red-400"
                title="Revoke invitation"
              >
                <XCircle size={14} />
                Revoke
              </button>
            </div>
          </div>
        {/each}
      </div>
    </div>
  {/if}

  <!-- Past invitations -->
  {#if pastInvitations.length > 0}
    <div
      class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
    >
      <div
        class="border-b border-surface-200 px-6 py-4 dark:border-surface-700"
      >
        <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
          Past Invitations <span class="text-xs font-normal text-surface-400">({pastInvitations.length})</span>
        </h2>
      </div>
      <div class="divide-y divide-surface-100 dark:divide-surface-700">
        {#each pastInvitations as inv (inv.id)}
          <div class="flex items-center justify-between px-6 py-4">
            <div>
              <p
                class="text-sm font-medium text-surface-900 dark:text-surface-50"
              >
                {inv.email}
              </p>
              <p class="text-xs text-surface-500">
                {inv.status} &middot; {formatDate(inv.createdAt)}
              </p>
            </div>
            <span
              class="rounded-full px-2 py-0.5 text-xs font-medium {inv.status ===
              'Accepted'
                ? 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400'
                : 'bg-surface-100 text-surface-500 dark:bg-surface-700 dark:text-surface-400'}"
            >
              {inv.status}
            </span>
          </div>
        {/each}
      </div>
    </div>
  {/if}
</div>

<ConfirmDialog
  open={revokeInviteConfirm.open}
  title="Revoke Invitation"
  message="Are you sure you want to revoke this invitation? The recipient will no longer be able to join your organization."
  confirmLabel="Revoke"
  variant="danger"
  onconfirm={() => {
    if (revokeInviteConfirm.id !== null) {
      revokeInvitation(revokeInviteConfirm.id);
    }
    revokeInviteConfirm = { open: false, id: null };
  }}
  oncancel={() => revokeInviteConfirm = { open: false, id: null }}
/>
