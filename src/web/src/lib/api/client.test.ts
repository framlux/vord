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
                pausedMachines: 1,
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

        it('should request machine ids with no query string when unfiltered', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () =>
                    Promise.resolve({
                        success: true,
                        data: { ids: [1, 2], totalCount: 2, truncated: false },
                        message: null,
                        errors: null
                    })
            });

            const result = await client.getMachineIds();

            expect(fetchFn.mock.calls[0][0]).toBe('http://localhost:12233/api/v1/machines/ids');
            expect(result).toEqual({ ids: [1, 2], totalCount: 2, truncated: false });
        });

        it('should pass the picker filters through to the machine ids endpoint', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () =>
                    Promise.resolve({
                        success: true,
                        data: { ids: [], totalCount: 0, truncated: false },
                        message: null,
                        errors: null
                    })
            });

            await client.getMachineIds({ search: 'web', healthStatus: 'Warning', os: 'Ubuntu' });

            const calledUrl = fetchFn.mock.calls[0][0] as string;
            expect(calledUrl).toContain('search=web');
            expect(calledUrl).toContain('healthStatus=Warning');
            expect(calledUrl).toContain('os=Ubuntu');
        });

        it('should surface truncation rather than hiding it behind the id list', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () =>
                    Promise.resolve({
                        success: true,
                        data: { ids: [1, 2, 3], totalCount: 900, truncated: true },
                        message: null,
                        errors: null
                    })
            });

            const result = await client.getMachineIds();

            // A caller reading only `ids` would believe it had the whole match. The flag is the
            // difference between an incomplete selection and a silently wrong one.
            expect(result.truncated).toBe(true);
            expect(result.totalCount).toBe(900);
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

        it('should throw ApiError with 403 and a bare Forbidden when the body carries no message', async () => {
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

        it('should surface the message of a 403 error envelope', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 403,
                json: () =>
                    Promise.resolve({
                        success: false,
                        data: null,
                        message: 'Remote commands require a Team subscription',
                        errors: null
                    })
            });

            const error = await client.getAdminSettings().catch((e: unknown) => e);

            expect(error).toBeInstanceOf(ApiError);
            expect((error as ApiError).status).toBe(403);
            expect((error as ApiError).message).toBe('Remote commands require a Team subscription');
            expect(fetchFn).toHaveBeenCalledTimes(1);
        });

        it.each([
            ['a null message', { success: false, data: null, message: null, errors: null }],
            ['an empty message', { success: false, data: null, message: '', errors: null }],
            ['a blank message', { success: false, data: null, message: '   ', errors: null }],
            ['a message that is not a string', { success: false, data: null, message: 42, errors: null }],
            ['a body that is not an object', 'denied'],
            ['a null body', null]
        ])('should give a bare Forbidden for a 403 whose body has %s', async (_name, body) => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 403,
                json: () => Promise.resolve(body)
            });

            const error = await client.getAdminSettings().catch((e: unknown) => e);

            expect(error).toBeInstanceOf(ApiError);
            expect((error as ApiError).status).toBe(403);
            expect((error as ApiError).message).toBe('Forbidden');
        });

        it('should give a bare Forbidden for a 403 with no body or one that is not JSON', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 403,
                statusText: 'Forbidden',
                json: () => Promise.reject(new SyntaxError('Unexpected end of JSON input'))
            });

            const error = await client.getAdminSettings().catch((e: unknown) => e);

            expect(error).toBeInstanceOf(ApiError);
            expect((error as ApiError).status).toBe(403);
            expect((error as ApiError).message).toBe('Forbidden');
            expect(fetchFn).toHaveBeenCalledTimes(1);
        });

        it('should keep a 401 unauthorized whatever its body says', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 401,
                json: () => Promise.resolve({ success: false, data: null, message: 'Token expired', errors: null })
            });

            const error = await client.getMe().catch((e: unknown) => e);

            expect((error as ApiError).status).toBe(401);
            expect((error as ApiError).message).toBe('Unauthorized');
        });

        it('should keep a 404 not found whatever its body says', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 404,
                json: () => Promise.resolve({ success: false, data: null, message: 'Machine 999 does not exist', errors: null })
            });

            const error = await client.getMachine(999).catch((e: unknown) => e);

            expect((error as ApiError).status).toBe(404);
            expect((error as ApiError).message).toBe('Not found');
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

        it('should retry 5xx errors and throw after max retries', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 500,
                json: () => Promise.resolve({ success: false, data: null, message: null, errors: null })
            });

            try {
                await client.getMe();
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(500);
                expect((e as ApiError).message).toBe('Server error (500)');
                // Should have retried 3 times
                expect(fetchFn).toHaveBeenCalledTimes(3);
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
        it('should throw ApiError with statusText when response body is not JSON on non-5xx', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                statusText: 'OK',
                json: () => Promise.reject(new SyntaxError('Unexpected token'))
            });

            try {
                await client.getMe();
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(200);
                expect((e as ApiError).message).toBe('Request failed with status 200 (OK)');
            }
        });

        it('should retry and throw server error for 5xx responses', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
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
                expect((e as ApiError).message).toBe('Server error (502)');
                expect(fetchFn).toHaveBeenCalledTimes(3);
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

        it('should GET /api/v1/clusters and return the cluster list', async () => {
            const clusters = [{ id: 4, name: 'k3s cp-1', displayName: null, distro: 'k3s', nodeCount: 3, managedNodeCount: 2, unmanagedNodeCount: 1, kubeletVersions: ['v1.31.4+k3s1'], reportingState: 'off', firstSeenAt: '2026-10-01T00:00:00Z', lastSeenAt: '2026-10-11T00:00:00Z' }];
            mockSuccess(clusters);
            const result = await client.getClusters();
            expect(result).toEqual(clusters);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/clusters',
                expect.objectContaining({ method: 'GET' })
            );
        });

        it('should GET /api/v1/clusters/{id} and return the cluster with its nodes', async () => {
            const cluster = { id: 4, name: 'k3s cp-1', caFingerprint: 'ab12', hasFingerprintMismatch: false, nodes: [] };
            mockSuccess(cluster);
            const result = await client.getCluster(4);
            expect(result).toEqual(cluster);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/clusters/4',
                expect.objectContaining({ method: 'GET' })
            );
        });

        it('should reject with a 404 ApiError when the cluster does not exist', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 404,
                statusText: 'Not Found',
                json: () => Promise.resolve({ success: false, data: null, message: 'Cluster not found', errors: null })
            });

            await expect(client.getCluster(99)).rejects.toMatchObject({ status: 404 });
        });

        it('should PATCH /api/v1/clusters/{id} with the display name and return the updated cluster', async () => {
            const updated = { id: 4, name: 'Production', displayName: 'Production' };
            mockSuccess(updated);
            const result = await client.renameCluster(4, 'Production');
            expect(result).toEqual(updated);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/clusters/4',
                expect.objectContaining({
                    method: 'PATCH',
                    body: JSON.stringify({ displayName: 'Production' })
                })
            );
        });

        it('should PATCH a null display name to reset the cluster to its default name', async () => {
            mockSuccess({ id: 4, name: 'k3s cp-1', displayName: null });
            await client.renameCluster(4, null);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/clusters/4',
                expect.objectContaining({
                    method: 'PATCH',
                    body: JSON.stringify({ displayName: null })
                })
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

        it('should POST /api/v1/commands with a null Params as JSON null, not an empty string', async () => {
            const command = { id: 1, commandId: 'c', machineId: 7, commandType: 'k8s_reporter_enrol', status: 'Pending' };
            mockSuccess(command);
            const request = {
                commandId: 'c',
                machineId: 7,
                signingKeyId: 41,
                commandType: 'k8s_reporter_enrol',
                params: null,
                nonce: 'n',
                signature: 's',
                canonicalPayload: '{"params":{}}',
                timestamp: '2026-10-12T09:30:00.000Z',
                expiresAt: '2026-10-12T09:40:00.000Z'
            };
            const result = await client.sendCommand(request);
            expect(result).toEqual(command);
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/commands',
                expect.objectContaining({ method: 'POST', body: JSON.stringify(request) })
            );
            expect(JSON.parse(fetchFn.mock.calls[0][1].body).params).toBeNull();
        });

        it('should POST to /api/v1/auth/email-lookup and return the opaque sso result', async () => {
            mockSuccess({ ssoAvailable: true, slug: 'opaque-slug-token' });
            const result = await client.emailLookup('user@company.com');
            expect(result).toEqual({ ssoAvailable: true, slug: 'opaque-slug-token' });
            expect(fetchFn).toHaveBeenCalledWith(
                'http://localhost:12233/api/v1/auth/email-lookup',
                expect.objectContaining({
                    method: 'POST',
                    body: JSON.stringify({ email: 'user@company.com' })
                })
            );
        });

        it('should return ssoAvailable false on a domain with no SSO', async () => {
            mockSuccess({ ssoAvailable: false, slug: null });
            const result = await client.emailLookup('user@no-sso.com');
            expect(result).toEqual({ ssoAvailable: false, slug: null });
        });
    });

    describe('CSRF token header', () => {
        it('should attach X-CSRF-TOKEN on POST when a token is set', async () => {
            const csrfClient = new ApiClient(
                'http://localhost:12233',
                fetchFn as unknown as typeof fetch,
                'csrf-token-abc'
            );
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: { tenantId: 1 }, message: null, errors: null })
            });

            await csrfClient.switchTenant(1);

            const init = fetchFn.mock.calls[0][1] as RequestInit;
            const headers = init.headers as Record<string, string>;
            expect(headers['X-CSRF-TOKEN']).toBe('csrf-token-abc');
        });

        it('should not attach X-CSRF-TOKEN on GET requests', async () => {
            const csrfClient = new ApiClient(
                'http://localhost:12233',
                fetchFn as unknown as typeof fetch,
                'csrf-token-abc'
            );
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: [], message: null, errors: null })
            });

            await csrfClient.getTenants();

            const init = fetchFn.mock.calls[0][1] as RequestInit;
            const headers = init.headers as Record<string, string>;
            expect(headers['X-CSRF-TOKEN']).toBeUndefined();
        });

        it('should not attach X-CSRF-TOKEN when no token is configured', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: {}, message: null, errors: null })
            });

            await client.switchTenant(1);

            const init = fetchFn.mock.calls[0][1] as RequestInit;
            const headers = init.headers as Record<string, string>;
            expect(headers['X-CSRF-TOKEN']).toBeUndefined();
        });

        it('should attach X-CSRF-TOKEN after setCsrfToken is called', async () => {
            client.setCsrfToken('late-token');
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: {}, message: null, errors: null })
            });

            await client.switchTenant(1);

            const init = fetchFn.mock.calls[0][1] as RequestInit;
            const headers = init.headers as Record<string, string>;
            expect(headers['X-CSRF-TOKEN']).toBe('late-token');
        });

        it('should attach X-CSRF-TOKEN on createOrganization after setCsrfToken is called', async () => {
            client.setCsrfToken('onboarding-token');
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: { tenantId: 1 }, message: null, errors: null })
            });

            await client.createOrganization({ organizationName: 'New Org' });

            const init = fetchFn.mock.calls[0][1] as RequestInit;
            const headers = init.headers as Record<string, string>;
            expect(headers['X-CSRF-TOKEN']).toBe('onboarding-token');
        });
    });

    // The antiforgery cookie can still be replaced behind the page's back (cleared cookies, a
    // cross-site arrival that did not carry the Strict cookie, a lost key ring). The gate rejects
    // before the endpoint runs, so repeating the request once with a freshly issued token is safe.
    describe('antiforgery rejection recovery', () => {
        const antiforgeryBody = { error: 'Antiforgery validation failed.' };

        function reply(status: number, body: unknown) {
            return {
                ok: status >= 200 && status < 300,
                status,
                statusText: status === 200 ? 'OK' : 'Error',
                json: () => Promise.resolve(body)
            };
        }

        function meReply(csrfToken: string | null) {
            return reply(200, {
                success: true,
                data: {
                    id: 1, name: 'Test', email: 'test@example.com', avatar: '', isGlobalAdmin: false,
                    uniqueId: 'abc', needsOnboarding: false, tenants: [], activeTenantId: 1, csrfToken
                },
                message: null,
                errors: null
            });
        }

        const okBody = { success: true, data: {}, message: null, errors: null };

        function headersOf(call: number): Record<string, string> {
            return (fetchFn.mock.calls[call][1] as RequestInit).headers as Record<string, string>;
        }

        it('should fetch a fresh token and retry a POST once, returning the retried response', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'stale-token');
            fetchFn
                .mockResolvedValueOnce(reply(400, antiforgeryBody))
                .mockResolvedValueOnce(meReply('fresh-token'))
                .mockResolvedValueOnce(reply(200, { success: true, data: { id: 9 }, message: null, errors: null }));

            const result = await csrfClient.authorizeMachineKey(5, 9);

            expect(result).toEqual({ id: 9 });
            expect(fetchFn).toHaveBeenCalledTimes(3);

            // The token is refreshed through the same base URL and fetch the client was built with,
            // which in the browser is the SvelteKit proxy.
            expect(fetchFn.mock.calls[1][0]).toBe('/api/v1/auth/me');
            expect((fetchFn.mock.calls[1][1] as RequestInit).method).toBe('GET');

            expect(fetchFn.mock.calls[2][0]).toBe(fetchFn.mock.calls[0][0]);
            expect((fetchFn.mock.calls[2][1] as RequestInit).method).toBe('POST');
            expect((fetchFn.mock.calls[2][1] as RequestInit).body).toBe((fetchFn.mock.calls[0][1] as RequestInit).body);
            expect(headersOf(0)['X-CSRF-TOKEN']).toBe('stale-token');
            expect(headersOf(2)['X-CSRF-TOKEN']).toBe('fresh-token');
        });

        it('should keep the refreshed token for later requests on the same client', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'stale-token');
            fetchFn
                .mockResolvedValueOnce(reply(400, antiforgeryBody))
                .mockResolvedValueOnce(meReply('fresh-token'))
                .mockResolvedValueOnce(reply(200, okBody))
                .mockResolvedValueOnce(reply(200, okBody));

            await csrfClient.switchTenant(1);
            await csrfClient.switchTenant(2);

            expect(fetchFn).toHaveBeenCalledTimes(4);
            expect(headersOf(3)['X-CSRF-TOKEN']).toBe('fresh-token');
        });

        it.each([
            ['POST', (c: ApiClient) => c.switchTenant(1)],
            ['PATCH', (c: ApiClient) => c.updateMachine(1, { name: 'web-01' })],
            ['DELETE', (c: ApiClient) => c.deleteMachine(1)],
            ['PUT', (c: ApiClient) => c.updateMachineAlertRules(1, [2])]
        ])('should retry a %s once with the fresh token', async (method, send) => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'stale-token');
            fetchFn
                .mockResolvedValueOnce(reply(400, antiforgeryBody))
                .mockResolvedValueOnce(meReply('fresh-token'))
                .mockResolvedValueOnce(reply(200, okBody));

            await send(csrfClient);

            expect(fetchFn).toHaveBeenCalledTimes(3);
            expect((fetchFn.mock.calls[0][1] as RequestInit).method).toBe(method);
            expect((fetchFn.mock.calls[2][1] as RequestInit).method).toBe(method);
            expect(headersOf(2)['X-CSRF-TOKEN']).toBe('fresh-token');
        });

        it('should surface a second antiforgery rejection without retrying again', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'stale-token');
            fetchFn
                .mockResolvedValueOnce(reply(400, antiforgeryBody))
                .mockResolvedValueOnce(meReply('fresh-token'))
                .mockResolvedValueOnce(reply(400, antiforgeryBody));

            const failure = await csrfClient.switchTenant(1).catch((e: unknown) => e);

            expect(failure).toBeInstanceOf(ApiError);
            expect((failure as ApiError).status).toBe(400);
            expect((failure as ApiError).message).toBe('Antiforgery validation failed.');
            expect(fetchFn).toHaveBeenCalledTimes(3);
        });

        it('should not retry a 400 whose body is a different error', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'token');
            fetchFn.mockResolvedValue(
                reply(400, { success: false, data: null, message: 'Name is required', errors: null })
            );

            const failure = await csrfClient.switchTenant(1).catch((e: unknown) => e);

            expect((failure as ApiError).status).toBe(400);
            expect((failure as ApiError).message).toBe('Name is required');
            expect(fetchFn).toHaveBeenCalledTimes(1);
        });

        it('should not retry a 400 whose error text only resembles the antiforgery one', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'token');
            fetchFn.mockResolvedValue(reply(400, { error: 'Validation failed.' }));

            await expect(csrfClient.switchTenant(1)).rejects.toBeInstanceOf(ApiError);

            expect(fetchFn).toHaveBeenCalledTimes(1);
        });

        it.each([
            [401, 'Unauthorized'],
            [403, 'Forbidden'],
            [404, 'Not found']
        ])('should not retry a %i response', async (status, message) => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'token');
            fetchFn.mockResolvedValue(reply(status, antiforgeryBody));

            const failure = await csrfClient.switchTenant(1).catch((e: unknown) => e);

            expect((failure as ApiError).status).toBe(status);
            expect((failure as ApiError).message).toBe(message);
            expect(fetchFn).toHaveBeenCalledTimes(1);
        });

        // Only the gate's own status is a rejection to recover from. The same body on any other 4xx
        // is some other refusal, and repeating the request could do real work twice.
        it.each([409, 422, 429])('should not retry a %i that carries the antiforgery body', async (status) => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'token');
            fetchFn.mockResolvedValue(reply(status, antiforgeryBody));

            const failure = await csrfClient.switchTenant(1).catch((e: unknown) => e);

            expect(failure).toBeInstanceOf(ApiError);
            expect((failure as ApiError).status).toBe(status);
            expect((failure as ApiError).message).toBe('Request failed');
            expect(fetchFn).toHaveBeenCalledTimes(1);
        });

        it('should never retry a GET, even for an antiforgery-shaped 400', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'token');
            fetchFn.mockResolvedValue(reply(400, antiforgeryBody));

            await expect(csrfClient.getTenants()).rejects.toBeInstanceOf(ApiError);

            expect(fetchFn).toHaveBeenCalledTimes(1);
        });

        it('should surface the original rejection when the refreshed session carries no token', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'stale-token');
            fetchFn
                .mockResolvedValueOnce(reply(400, antiforgeryBody))
                .mockResolvedValueOnce(meReply(null));

            const failure = await csrfClient.switchTenant(1).catch((e: unknown) => e);

            expect((failure as ApiError).status).toBe(400);
            expect((failure as ApiError).message).toBe('Antiforgery validation failed.');
            expect(fetchFn).toHaveBeenCalledTimes(2);
        });

        it('should surface the refresh failure when the session can no longer be read', async () => {
            const csrfClient = new ApiClient('', fetchFn as unknown as typeof fetch, 'stale-token');
            fetchFn
                .mockResolvedValueOnce(reply(400, antiforgeryBody))
                .mockResolvedValueOnce(reply(401, null));

            const failure = await csrfClient.switchTenant(1).catch((e: unknown) => e);

            expect((failure as ApiError).status).toBe(401);
            expect(fetchFn).toHaveBeenCalledTimes(2);
        });
    });

    describe('retry behavior', () => {
        it('should succeed on second attempt after transient 500', async () => {
            const userData = {
                id: 1, name: 'Test', email: 'test@example.com',
                avatar: '', isGlobalAdmin: false, uniqueId: 'abc',
                needsOnboarding: false, tenants: [], activeTenantId: 1
            };

            fetchFn
                .mockResolvedValueOnce({ ok: false, status: 500, json: () => Promise.resolve({}) })
                .mockResolvedValueOnce({
                    ok: true,
                    status: 200,
                    json: () => Promise.resolve({ success: true, data: userData, message: null, errors: null })
                });

            const result = await client.getMe();
            expect(result).toEqual(userData);
            expect(fetchFn).toHaveBeenCalledTimes(2);
        });

        it('should not retry 4xx errors', async () => {
            fetchFn.mockResolvedValue({
                ok: false,
                status: 422,
                json: () => Promise.resolve({ success: false, data: null, message: 'Validation failed', errors: null })
            });

            try {
                await client.getMe();
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(422);
                // Should only be called once since 4xx is not retried
                expect(fetchFn).toHaveBeenCalledTimes(1);
            }
        });

        it('should include abort signal for timeout', async () => {
            const userData = {
                id: 1, name: 'Test', email: 'test@example.com',
                avatar: '', isGlobalAdmin: false, uniqueId: 'abc',
                needsOnboarding: false, tenants: [], activeTenantId: 1
            };

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve({ success: true, data: userData, message: null, errors: null })
            });

            await client.getMe();
            expect(fetchFn).toHaveBeenCalledWith(
                expect.any(String),
                expect.objectContaining({
                    signal: expect.any(AbortSignal)
                })
            );
        });

        it('should retry on abort timeout error', async () => {
            const abortError = new DOMException('The operation was aborted', 'AbortError');

            const userData = {
                id: 1, name: 'Test', email: 'test@example.com',
                avatar: '', isGlobalAdmin: false, uniqueId: 'abc',
                needsOnboarding: false, tenants: [], activeTenantId: 1
            };

            fetchFn
                .mockRejectedValueOnce(abortError)
                .mockResolvedValueOnce({
                    ok: true,
                    status: 200,
                    json: () => Promise.resolve({ success: true, data: userData, message: null, errors: null })
                });

            const result = await client.getMe();
            expect(result).toEqual(userData);
            expect(fetchFn).toHaveBeenCalledTimes(2);
        });

        it('should throw timeout error after all retries exhausted', async () => {
            const abortError = new DOMException('The operation was aborted', 'AbortError');
            fetchFn.mockRejectedValue(abortError);

            try {
                await client.getMe();
                expect.unreachable('Expected ApiError to be thrown');
            } catch (e) {
                expect(e).toBeInstanceOf(ApiError);
                expect((e as ApiError).status).toBe(0);
                expect((e as ApiError).message).toBe('Request timed out');
                expect(fetchFn).toHaveBeenCalledTimes(3);
            }
        });
    });

    describe('getMeBootstrap CSRF cookie extraction', () => {
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
        const meResponse: ApiResponse<UserDto> = {
            success: true,
            data: userData,
            message: null,
            errors: null
        };

        it('should extract vord_csrf from getSetCookie() when available', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(meResponse),
                headers: {
                    getSetCookie: () => [
                        'vord_auth=session-value; Path=/; HttpOnly',
                        'vord_csrf=token-abc; Path=/; Secure; SameSite=Strict'
                    ],
                    get: () => null
                }
            });

            const result = await client.getMeBootstrap();
            expect(result.user).toEqual(userData);
            expect(result.csrfCookie).toBe('token-abc');
        });

        it('should extract vord_csrf from a folded set-cookie header when getSetCookie is unavailable and vord_csrf is not first', async () => {
            // Fetch spec folds multiple Set-Cookie headers into one comma-joined string when
            // getSetCookie() is unavailable. The Expires date itself contains a comma, so the
            // parser must split only on the cookie-name boundary.
            const folded =
                'vord_auth=session-value; Path=/; Expires=Wed, 21 Oct 2026 07:28:00 GMT; HttpOnly, ' +
                'vord_csrf=token-xyz; Path=/; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Secure; SameSite=Strict';

            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(meResponse),
                headers: {
                    get: (name: string) => (name === 'set-cookie' ? folded : null)
                }
            });

            const result = await client.getMeBootstrap();
            expect(result.user).toEqual(userData);
            expect(result.csrfCookie).toBe('token-xyz');
        });

        it('should return undefined csrfCookie when no set-cookie header is present', async () => {
            fetchFn.mockResolvedValue({
                ok: true,
                status: 200,
                json: () => Promise.resolve(meResponse),
                headers: {
                    get: () => null
                }
            });

            const result = await client.getMeBootstrap();
            expect(result.user).toEqual(userData);
            expect(result.csrfCookie).toBeUndefined();
        });
    });
});
