// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
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

// GetRaw reads one object as a generic map. Numbers decode as float64, which is exact for the integers
// Kubernetes uses. A response that is not a JSON object is an error.
//
// Used with PutRaw it makes a read-modify-write that keeps every field the agent does not know, such as the
// fields a newer API version adds.
func (c *Client) GetRaw(ctx context.Context, path string) (map[string]any, error) {
	return c.objectCall(ctx, http.MethodGet, path, nil)
}

// PutRaw replaces one object with obj and returns the stored object. A stale metadata.resourceVersion in obj
// fails with a Conflict, which makes the write a compare-and-swap.
func (c *Client) PutRaw(ctx context.Context, path string, obj map[string]any) (map[string]any, error) {
	return c.objectCall(ctx, http.MethodPut, path, obj)
}

func (c *Client) objectCall(ctx context.Context, method string, path string, body map[string]any) (map[string]any, error) {
	var payload any
	if body != nil {
		payload = body
	}

	var obj map[string]any
	if err := c.Do(ctx, method, path, nil, payload, &obj); err != nil {
		return nil, err
	}

	if obj == nil {
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
// continue token, and calls page once with the body of each page. Memory is bounded by one page.
//
// When the server expires the continue token (410) List starts again from page one, at most 3 times, then returns
// the 410. A restart does not retract what page already received: page one is delivered again, so a callback
// that accumulates must tolerate repeats. An error from page stops the listing and is returned as is. query is
// not modified.
func (c *Client) List(ctx context.Context, path string, query url.Values, limit int, page func(body []byte) error) error {
	for restarts := 0; ; restarts++ {
		expired, err := c.listPass(ctx, path, query, limit, page)
		if (expired == false) || (restarts >= maxListRestarts) {
			return err
		}
	}
}

// listPass walks the collection once. Its bool is true when a continue token was refused with a 410, which is
// the only failure that a fresh pass can cure.
func (c *Client) listPass(ctx context.Context, path string, query url.Values, limit int, page func(body []byte) error) (bool, error) {
	token := ""

	for {
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

		if err := page(body); err != nil {
			return false, err
		}

		var parsed listPage
		if err := json.Unmarshal(body, &parsed); err != nil {
			return false, fmt.Errorf("decode list response: %w", err)
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
