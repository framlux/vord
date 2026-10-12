// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
)

// maxListRestarts is how many times List starts over after the server expires its continue token.
const maxListRestarts = 3

// maxListPages is how many pages one List call may fetch, restarts included. A collection that needs more is
// either enormous or being fed to the agent by a server that never ends the list.
const maxListPages = 10_000

// GetRaw reads one object as a generic map. A response that is not a JSON object is an error. Numbers decode as
// json.Number, which keeps every integer exact, so an object read here and written back with PutRaw is never
// altered by the round trip.
//
// Used with PutRaw it makes a read-modify-write that keeps every field the agent does not know, such as the
// fields a newer API version adds.
func (c *Client) GetRaw(ctx context.Context, path string) (map[string]any, error) {
	return c.objectCall(ctx, http.MethodGet, path, nil)
}

// PutRaw replaces one object with obj and returns the stored object, decoded as GetRaw does. A stale
// metadata.resourceVersion in obj fails with a Conflict, which makes the write a compare-and-swap.
func (c *Client) PutRaw(ctx context.Context, path string, obj map[string]any) (map[string]any, error) {
	return c.objectCall(ctx, http.MethodPut, path, obj)
}

func (c *Client) objectCall(ctx context.Context, method string, path string, body map[string]any) (map[string]any, error) {
	var payload any
	if body != nil {
		payload = body
	}

	data, err := c.roundTrip(ctx, method, path, nil, payload)
	if err != nil {
		return nil, err
	}

	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()

	var obj map[string]any
	if err := decoder.Decode(&obj); (err != nil) || (obj == nil) || decoder.More() {
		return nil, fmt.Errorf("%s %s response is not a JSON object", method, path)
	}

	return obj, nil
}

// listPage is the part of a list response List needs.
type listPage struct {
	Metadata struct {
		Continue string `json:"continue"`
	} `json:"metadata"`
}

// List reads a collection one page at a time, with the given limit (omitted when zero or less) and each page's
// continue token, and calls page once with the body of each page. Memory is bounded by one page, and a body that
// is not valid JSON is an error that page never sees.
//
// When the server expires the continue token (410) List starts again from page one, at most 3 times, then
// returns the 410. Before page one is delivered again it calls onRestart (which may be nil), so a caller that
// accumulates across pages can discard what the abandoned pass gave it. An error from page stops the listing and
// is returned as is. One call fetches at most 10,000 pages in total, restarts included, and then fails. query is
// not modified.
func (c *Client) List(ctx context.Context, path string, query url.Values, limit int, page func(body []byte) error, onRestart func()) error {
	pages := 0

	for restarts := 0; ; restarts++ {
		expired, err := c.listPass(ctx, path, query, limit, page, &pages)
		if (expired == false) || (restarts >= maxListRestarts) {
			return err
		}

		if onRestart != nil {
			onRestart()
		}
	}
}

// listPass walks the collection once, adding each page it fetches to pages. Its bool is true when a continue
// token was refused with a 410, which is the only failure that a fresh pass can cure.
func (c *Client) listPass(ctx context.Context, path string, query url.Values, limit int, page func(body []byte) error, pages *int) (bool, error) {
	token := ""

	for {
		if *pages >= c.maxPages {
			return false, fmt.Errorf("the list has more than %d pages", c.maxPages)
		}

		pageQuery := url.Values{}
		for key, values := range query {
			pageQuery[key] = append([]string(nil), values...)
		}

		if limit > 0 {
			pageQuery.Set("limit", strconv.Itoa(limit))
		}

		if token != "" {
			pageQuery.Set("continue", token)
		}

		body, err := c.roundTrip(ctx, http.MethodGet, path, pageQuery, nil)
		if err != nil {
			apiError := apiErrorOf(err)

			return (token != "") && (apiError != nil) && (apiError.Code == http.StatusGone), err
		}

		*pages++

		var parsed listPage
		if err := json.Unmarshal(body, &parsed); err != nil {
			return false, fmt.Errorf("decode list response: %w", err)
		}

		if err := page(body); err != nil {
			return false, err
		}

		next := parsed.Metadata.Continue
		if next == "" {
			return false, nil
		}

		if next == token {
			return false, errors.New("the list continue token did not advance")
		}

		token = next
	}
}
