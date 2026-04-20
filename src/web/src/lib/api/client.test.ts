// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ApiClient, ApiError } from './client';
import type { ApiResponse, UserDto, DashboardSummaryDto, PaginatedResponse, MachineDto } from './types';

function mockFetch(status: number, body: unknown): typeof fetch {
    return vi.fn().mockResolvedValue({
        ok: status >= 200 && status < 300,
        status,
        statusText: status === 200 ? 'OK' : 'Error',
        json: () => Promise.resolve(body)
    }) as unknown as typeof fetch;
}

describe('ApiClient', () => {
    let client: ApiClient;
    let fetchFn: ReturnType<typeof vi.fn>;

    beforeEach(() => {
        fetchFn = vi.fn();
        client = new ApiClient('http://localhost:12233', fetchFn as unknown as typeof fetch);
    });

    describe('constructor', () => {
        it('should strip trailing slash from base URL', () => {
            const mockFn = mockFetch(200, { success: true, data: { id: 1 }, message: null, errors: null });
            const c = new ApiClient('http://localhost:12233/', mockFn);
            c.getMe();
            expect(mockFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/auth/me',
                expect.any(Object)
            );
        });
    });

    describe('successful API calls', () => {
        it('should return user data from getMe', async () => {
            const userData: UserDto = {
                id: 1,
                name: 'Test User',
                email: 'test@example.com',
                avatar: 'https://example.com/avatar.png',
                isGlobalAdmin: false,
                uniqueId: 'abc-123',
                needsOnboarding: false,
                tenants: [{ tenantId: 1, tenantName: 'Test Org', role: '1' }],
                activeTenantId: 1
            };
            const response: ApiResponse<UserDto> = {
                success: true,
                data: userData,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            const result = await client.getMe();
            expect(result).toEqual(userData);
        });

        it('should call GET /api/v1/auth/me with correct headers', async () => {
            const userData: UserDto = {
                id: 1,
                name: 'Test User',
                email: 'test@example.com',
                avatar: 'https://example.com/avatar.png',
                isGlobalAdmin: false,
                uniqueId: 'abc-123',
                needsOnboarding: false,
                tenants: [{ tenantId: 1, tenantName: 'Test Org', role: '1' }],
                activeTenantId: 1
            };
            const response: ApiResponse<UserDto> = {
                success: true,
                data: userData,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            await client.getMe();
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/auth/me',
                expect.objectContaining({
                    method: 'GET',
                    headers: { Accept: 'application/json' },
                    credentials: 'include'
                })
            );
        });

        it('should fetch dashboard summary', async () => {
            const summaryData: DashboardSummaryDto = {
                totalMachines: 10,
                onlineMachines: 7,
                pendingApprovals: 2,
            };
            const response: ApiResponse<DashboardSummaryDto> = {
                success: true,
                data: summaryData,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            const result = await client.getDashboardSummary();
            expect(result).toEqual(summaryData);
        });

        it('should return data from createOrganization', async () => {
            const response: ApiResponse<{ tenantId: number }> = {
                success: true,
                data: { tenantId: 42 },
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            const result = await client.createOrganization({
                organizationName: 'New Org'
            });

            expect(result).toEqual({ tenantId: 42 });
        });

        it('should send POST with JSON body to create-org endpoint', async () => {
            const response: ApiResponse<{ tenantId: number }> = {
                success: true,
                data: { tenantId: 42 },
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            await client.createOrganization({
                organizationName: 'New Org'
            });

            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/onboarding/create-org',
                expect.objectContaining({
                    method: 'POST',
                    headers: {
                        Accept: 'application/json',
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ organizationName: 'New Org' }),
                    credentials: 'include'
                })
            );
        });

        it('should build query string for getMachines', async () => {
            const paginatedData: PaginatedResponse<MachineDto> = {
                items: [],
                page: 1,
                pageSize: 25,
                totalCount: 0,
                totalPages: 0,
                hasNextPage: false,
                hasPreviousPage: false
            };
            const response: ApiResponse<PaginatedResponse<MachineDto>> = {
                success: true,
                data: paginatedData,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            const result = await client.getMachines({ page: 2, pageSize: 10, search: 'server' });

            const calledUrl = new URL(fetchFn.mock.calls[0][0] as string);
            expect(calledUrl.pathname).toBe('/api/v1/machines');
            expect(calledUrl.searchParams.get('page')).toBe('2');
            expect(calledUrl.searchParams.get('pageSize')).toBe('10');
            expect(calledUrl.searchParams.get('search')).toBe('server');
            expect(result).toEqual(paginatedData);
        });

        it('should call getMachines without query string when no params', async () => {
            const paginatedData: PaginatedResponse<MachineDto> = {
                items: [],
                page: 1,
                pageSize: 25,
                totalCount: 0,
                totalPages: 0,
                hasNextPage: false,
                hasPreviousPage: false
            };
            const response: ApiResponse<PaginatedResponse<MachineDto>> = {
                success: true,
                data: paginatedData,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            await client.getMachines();

            const calledUrl = fetchFn.mock.calls[0][0] as string;
            expect(calledUrl).toBe('http://localhost:12233/api/v1/machines');
        });

        it('should send DELETE requests for deleteMachine', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: {}, message: null, errors: null })
            });

            await client.deleteMachine(5);

            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/machines/5',
                expect.objectContaining({
                    method: 'DELETE'
                })
            );
        });
    });

    describe('error handling', () => {
        it('should throw ApiError with 401 for unauthorized', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 401,
                json: () => Promise.resolve({})
            });

            try {
                await client.getMe();
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(401);
                expect((e as ApiError).message).toBe('Unauthorized');
            }
        });

        it('should throw ApiError with 403 for forbidden', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 403,
                json: () => Promise.resolve({})
            });

            try {
                await client.getAdminSettings();
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(403);
                expect((e as ApiError).message).toBe('Forbidden');
            }
        });

        it('should throw ApiError with 404 for not found', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 404,
                json: () => Promise.resolve({})
            });

            try {
                await client.getMachine(999);
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(404);
                expect((e as ApiError).message).toBe('Not found');
            }
        });

        it('should throw ApiError for non-ok responses with message from body', async () => {
            const errorResponse: ApiResponse<unknown> = {
                success: false,
                data: null,
                message: 'Machine limit exceeded',
                errors: ['Upgrade your subscription']
            };

            fetchFn.mockResolvedValue({
                ok: false,
                status: 422,
                json: () => Promise.resolve(errorResponse)
            });

            try {
                await client.getMachines();
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(422);
                expect((e as ApiError).message).toBe('Machine limit exceeded');
            }
        });

        it('should fallback to default error message when body has no message', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 500,
                json: () => Promise.resolve({ success: false, data: null, message: null, errors: null })
            });

            try {
                await client.getMe();
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(500);
                expect((e as ApiError).message).toBe('Request failed');
            }
        });
    });

    describe('response unwrapping', () => {
        it('should throw when success is false', async () => {
            const response: ApiResponse<UserDto> = {
                success: false,
                data: null,
                message: 'Something went wrong',
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            await expect(client.getMe()).rejects.toThrow('Something went wrong');
        });

        it('should throw when data is null even if success is true', async () => {
            const response: ApiResponse<UserDto> = {
                success: false,
                data: null,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            await expect(client.getMe()).rejects.toThrow('Unknown error');
        });

        it('should return data when success is true and data is present', async () => {
            const tenants = [
                { id: 1, name: 'Org A', logoUrl: '', isActive: true },
                { id: 2, name: 'Org B', logoUrl: '', isActive: false }
            ];
            const response: ApiResponse<typeof tenants> = {
                success: true,
                data: tenants,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            const result = await client.getTenants();
            expect(result).toEqual(tenants);
            expect(result).toHaveLength(2);
        });
    });

    describe('invitations', () => {
        it('should return invitation data from createInvitation', async () => {
            const invitation = {
                id: 1,
                email: 'new@example.com',
                token: 'tok-abc',
                acceptUrl: 'https://app.example.com/invite/tok-abc',
                expiresAt: '2026-03-01T00:00:00Z',
                status: 'Pending'
            };
            const response: ApiResponse<typeof invitation> = {
                success: true,
                data: invitation,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            const result = await client.createInvitation('new@example.com');
            expect(result).toEqual(invitation);
        });

        it('should send POST with email to invitations endpoint', async () => {
            const invitation = {
                id: 1,
                email: 'new@example.com',
                token: 'tok-abc',
                acceptUrl: 'https://app.example.com/invite/tok-abc',
                expiresAt: '2026-03-01T00:00:00Z',
                status: 'Pending'
            };
            const response: ApiResponse<typeof invitation> = {
                success: true,
                data: invitation,
                message: null,
                errors: null
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(response)
            });

            await client.createInvitation('new@example.com');
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/invitations',
                expect.objectContaining({
                    method: 'POST',
                    body: JSON.stringify({ email: 'new@example.com' })
                })
            );
        });
    });

    describe('tenant switching', () => {
        it('should post tenantId when switching tenants', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: {}, message: null, errors: null })
            });

            await client.switchTenant(5);

            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/tenants/switch',
                expect.objectContaining({
                    method: 'POST',
                    body: JSON.stringify({ tenantId: 5 })
                })
            );
        });
    });

    describe('network failures', () => {
        it('should propagate fetch TypeError on network failure', async () => {
            fetchFn.mockRejectedValue(new TypeError('Failed to fetch'));

            await expect(client.getMe()).rejects.toThrow('Failed to fetch');
        });
    });

    describe('JSON parse failure', () => {
        it('should throw ApiError with statusText when response body is not JSON', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 502,
                statusText: 'Bad Gateway',
                json: () => Promise.reject(new SyntaxError('Unexpected token'))
            });

            try {
                await client.getMe();
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(502);
                expect((e as ApiError).message).toBe('Request failed with status 502 (Bad Gateway)');
            }
        });
    });

    describe('remaining API methods', () => {
        function mockSuccess(data: unknown = {}) {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data, message: null, errors: null })
            });
        }

        it('should POST to /api/v1/logout and complete successfully for logout', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 204,
                statusText: 'No Content',
                json: () => Promise.reject(new SyntaxError('No content'))
            });

            await expect(client.logout()).rejects.toThrow(ApiError);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/logout',
                expect.objectContaining({ method: 'POST' })
            );
        });

        it('should throw ApiError with status details when logout returns non-JSON', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 204,
                statusText: 'No Content',
                json: () => Promise.reject(new SyntaxError('No content'))
            });

            try {
                await client.logout();
                expect.unreachable('Expected ApiError');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(204);
                expect((e as ApiError).message).toBe('Request failed with status 204 (No Content)');
            }
        });

        it('should GET /api/v1/billing/subscription and return subscription data', async () => {
            const subData = { tier: 'Pro', status: 'Active', machineLimit: 50, machineCount: 3, retentionDays: 90, currentPeriodEnd: null, cancelAtPeriodEnd: false };
            mockSuccess(subData);
            const result = await client.getSubscription();
            expect(result).toEqual(subData);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/billing/subscription',
                expect.objectContaining({ method: 'GET' })
            );
        });

        it('should GET /api/v1/dashboard/fleet with parsed query params for getFleetOverview', async () => {
            const fleetData = { summary: {}, machines: [], page: 2, pageSize: 25, totalCount: 0, totalPages: 0 };
            mockSuccess(fleetData);
            const result = await client.getFleetOverview({ page: 2, search: 'web' });

            const calledUrl = new URL(fetchFn.mock.calls[0][0] as string);
            expect(calledUrl.pathname).toBe('/api/v1/dashboard/fleet');
            expect(calledUrl.searchParams.get('page')).toBe('2');
            expect(calledUrl.searchParams.get('search')).toBe('web');
            expect(result).toEqual(fleetData);
        });

        it('should GET /api/v1/dashboard/fleet without query string when no params', async () => {
            const fleetData = { summary: {}, machines: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 };
            mockSuccess(fleetData);
            const result = await client.getFleetOverview();
            const calledUrl = fetchFn.mock.calls[0][0] as string;
            expect(calledUrl).toBe('http://localhost:12233/api/v1/dashboard/fleet');
            expect(result).toEqual(fleetData);
        });

        it('should GET /api/v1/machines/{id}/detail and return machine detail', async () => {
            const detailData = { id: 3, name: 'web-01', hostname: 'web-01.local' };
            mockSuccess(detailData);
            const result = await client.getMachineDetail(3);
            expect(result).toEqual(detailData);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/machines/3/detail',
                expect.objectContaining({ method: 'GET' })
            );
        });

        it('should GET /api/v1/machines/{id}/status and return status data', async () => {
            const statusData = { isOnline: true, lastPing: '2026-03-15T10:00:00Z' };
            mockSuccess(statusData);
            const result = await client.getMachineStatus(7);
            expect(result).toEqual(statusData);
        });

        it('should GET /api/v1/machines/registration-tokens and return tokens array', async () => {
            const tokens = [{ id: 1, name: 'Token 1', token: 'tok-123', expiresAt: '', maxUses: 10, usedCount: 0, createdAt: '', isRevoked: false }];
            mockSuccess({ items: tokens, page: 1, pageSize: 25, totalCount: 1 });
            const result = await client.getRegistrationTokens();
            expect(result).toEqual(tokens);
            expect(result).toHaveLength(1);
        });

        it('should POST to /api/v1/machines/registration-tokens for createRegistrationToken', async () => {
            const req = { name: 'Token 1', expiresInDays: 30, maxUses: 10 };
            const tokenData = { id: 1, name: 'Token 1', token: 'tok-123', expiresAt: '', maxUses: 10, usedCount: 0, createdAt: '', isRevoked: false };
            mockSuccess(tokenData);
            const result = await client.createRegistrationToken(req);
            expect(result).toEqual(tokenData);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/machines/registration-tokens',
                expect.objectContaining({
                    method: 'POST',
                    body: JSON.stringify(req)
                })
            );
        });

        it('should DELETE /api/v1/machines/registration-tokens/{id} for revokeRegistrationToken', async () => {
            mockSuccess({});
            await client.revokeRegistrationToken(8);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/machines/registration-tokens/8',
                expect.objectContaining({ method: 'DELETE' })
            );
        });

        it('should GET /api/v1/users and return users array', async () => {
            const users = [{ id: 1, username: 'alice', externalId: 'ext-1', isActive: true, isGlobalAdmin: false, createdAt: '', tenants: [] }];
            mockSuccess(users);
            const result = await client.getUsers();
            expect(result).toEqual(users);
            expect(result).toHaveLength(1);
        });

        it('should GET /api/v1/users/{id} and return user data', async () => {
            const user = { id: 2, username: 'alice', externalId: 'ext-2', isActive: true, isGlobalAdmin: false, createdAt: '', tenants: [] };
            mockSuccess(user);
            const result = await client.getUser(2);
            expect(result).toEqual(user);
        });

        it('should POST to /api/v1/users/{id}/deactivate for deactivateUser', async () => {
            mockSuccess({});
            await client.deactivateUser(3);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/users/3/deactivate',
                expect.objectContaining({ method: 'POST' })
            );
        });

        it('should GET /api/v1/tenants/{id} and return tenant data', async () => {
            const tenant = { id: 1, name: 'Org', logoUrl: '', isActive: true };
            mockSuccess(tenant);
            const result = await client.getTenant(1);
            expect(result).toEqual(tenant);
        });

        it('should POST to /api/v1/tenants and return created tenant', async () => {
            const data = { name: 'New Org', logoUrl: 'https://example.com/logo.png' };
            const tenantData = { id: 5, ...data, isActive: true };
            mockSuccess(tenantData);
            const result = await client.createTenant(data);
            expect(result).toEqual(tenantData);
        });

        it('should GET /api/v1/admin/users and return admin users array', async () => {
            const users = [{ id: 1, username: 'admin', externalId: 'ext-1', isActive: true, isGlobalAdmin: true, createdAt: '', tenants: [] }];
            mockSuccess(users);
            const result = await client.getAdminUsers();
            expect(result).toEqual(users);
            expect(result).toHaveLength(1);
        });

        it('should GET /api/v1/invitations and return invitations array', async () => {
            const invitations = [{ id: 1, email: 'a@b.com', status: 'Pending', createdAt: '', expiresAt: '', role: 'Viewer' }];
            mockSuccess(invitations);
            const result = await client.getInvitations();
            expect(result).toEqual(invitations);
            expect(result).toHaveLength(1);
        });

        it('should POST to /api/v1/invitations/{id}/revoke for revokeInvitation', async () => {
            mockSuccess({});
            await client.revokeInvitation(10);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/invitations/10/revoke',
                expect.objectContaining({ method: 'POST' })
            );
        });

        it('should POST to /api/v1/invitations/{id}/resend and return invitation', async () => {
            const invitationData = { id: 10, email: 'a@b.com', token: 't', acceptUrl: '', expiresAt: '', status: 'Pending' };
            mockSuccess(invitationData);
            const result = await client.resendInvitation(10);
            expect(result).toEqual(invitationData);
        });

        it('should GET /api/v1/invitations/by-token/{token} and return invitation detail', async () => {
            const detailData = { tenantName: 'Org', inviterEmail: 'a@b.com', email: 'c@d.com', expiresAt: '', status: 'Pending' };
            mockSuccess(detailData);
            const result = await client.getInvitationByToken('tok-abc');
            expect(result).toEqual(detailData);
        });

        it('should POST to /api/v1/invitations/{token}/accept and return tenantId', async () => {
            mockSuccess({ tenantId: 7 });
            const result = await client.acceptInvitation('tok-xyz');
            expect(result).toEqual({ tenantId: 7 });
        });

        it('should GET /api/v1/members and return members array', async () => {
            const members = [{ userId: 1, email: 'a@b.com', role: 'Admin', joinedAt: '' }];
            mockSuccess(members);
            const result = await client.getMembers();
            expect(result).toEqual(members);
            expect(result).toHaveLength(1);
        });

        it('should POST to /api/v1/members/{userId}/remove for removeMember', async () => {
            mockSuccess({});
            await client.removeMember(12);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/members/12/remove',
                expect.objectContaining({ method: 'POST' })
            );
        });

        it('should GET /api/v1/machines/{id}/authorized-keys and return keys array', async () => {
            const keys = [{ id: 1, signingKeyId: 5, label: 'Test', fingerprint: 'abc', ownerUsername: 'user', authorizedAt: '', authorizedByUsername: 'admin', revokedAt: null, isActive: true }];
            mockSuccess(keys);
            const result = await client.getMachineAuthorizedKeys(7);
            expect(result).toEqual(keys);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/machines/7/authorized-keys',
                expect.objectContaining({ method: 'GET' })
            );
        });

        it('should POST /api/v1/machines/{id}/authorized-keys and return authorized key', async () => {
            const key = { id: 1, signingKeyId: 5, label: 'Test', fingerprint: 'abc', ownerUsername: 'user', authorizedAt: '', authorizedByUsername: 'admin', revokedAt: null, isActive: true };
            mockSuccess(key);
            const result = await client.authorizeMachineKey(7, 5);
            expect(result).toEqual(key);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/machines/7/authorized-keys',
                expect.objectContaining({
                    method: 'POST',
                    body: JSON.stringify({ signingKeyId: 5 })
                })
            );
        });

        it('should DELETE /api/v1/machines/{id}/authorized-keys/{keyId}', async () => {
            mockSuccess(true);
            await client.revokeMachineKeyAuthorization(7, 5);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/machines/7/authorized-keys/5',
                expect.objectContaining({ method: 'DELETE' })
            );
        });

        it('should POST to /api/v1/auth/email-lookup and return tenantId', async () => {
            mockSuccess({ tenantId: 42 });
            const result = await client.emailLookup('user@company.com');
            expect(result).toEqual({ tenantId: 42 });
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/auth/email-lookup',
                expect.objectContaining({
                    method: 'POST',
                    body: JSON.stringify({ email: 'user@company.com' })
                })
            );
        });
    });
});
